using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;

namespace Ectype
{
    /// <summary>The owner-evaluated conditions, one bit each in a holder's condition mask. The
    /// bit values are part of the network contract.</summary>
    [Flags]
    internal enum HolderCondition
    {
        None = 0,
        /// <summary>Aboard their own crew ship, dying or not.</summary>
        OnOwnShip = 1,
        /// <summary>Aboard a living ship of their side that is not their crew ship.</summary>
        OnAllyShip = 2,
        /// <summary>Aboard a ship of another side.</summary>
        OnEnemyShip = 4,
        /// <summary>The character is aboard no ship.</summary>
        InOpenSpace = 8,
        /// <summary>A team match is running.</summary>
        TeamMatch = 16,
        /// <summary>Their crew ship exists and has not started dying.</summary>
        OwnShipAlive = 32,
        /// <summary>No character, or a multi-ship provider places them on no ship.</summary>
        Spectating = 64,
    }

    /// <summary>A perk's or an ability's conditions: bits that must be set and bits that must be
    /// clear in the holder's mask. Empty holds always.</summary>
    internal struct ConditionSet
    {
        internal int Require;
        internal int Forbid;

        internal bool Empty { get { return Require == 0 && Forbid == 0; } }

        /// <summary>Whether a mask satisfies the set. An unknown mask (-1) satisfies only the
        /// empty set.</summary>
        internal bool Holds(int mask)
        {
            if (Empty) return true;
            if (mask < 0) return false;
            return (mask & Require) == Require && (mask & Forbid) == 0;
        }
    }

    /// <summary>
    /// Conditions on a holder's own situation (where their character is, whether their ship
    /// lives, whether a team match runs) that gate manifest perk effects and role abilities.
    ///
    /// Owner-evaluated: only the holder's own client decides. It reads its own character
    /// through Topology four times a second, adopts a new answer once it has read the same value
    /// on two polls in a row (a character crossing an inertia-zone edge does not flicker the
    /// result), and sends the adopted answer to every peer running this network contract, only
    /// on a change, and once to each peer that joins later. Every client gates that holder's
    /// perk contributions on the answer it last received, so every client applies the same
    /// decision, a few frames behind the owner. Until a holder's first answer arrives a
    /// conditional contribution of theirs is held off.
    ///
    /// The answer is the condition mask and the number of the ship the character is aboard
    /// (0 for none). The aboard ship is the holder's to decide for the same reason the mask is:
    /// each client samples another player's character against an inertia bubble at its own
    /// cadence from an interpolated position, and the bubble's enter and stay radii differ, so
    /// two clients can disagree for as long as the character hovers at the edge. Every client
    /// resolves characterAboard from the number it received.
    ///
    /// Conditions on other ships' state ("an allied ship below 30% hull") need an edge broadcast
    /// by whoever observes it; validation refuses them.
    /// </summary>
    internal static class HolderConditions
    {
        internal const int AllBits = 127;
        /// <summary>The highest aboard ship number an answer may carry.</summary>
        internal const int MaxShipNumber = 0xFFFF;
        private const int ShipShift = 7;
        private const float PollSeconds = 0.25f;

        internal const string KnownConditions =
            "onOwnShip, onAllyShip, onEnemyShip, inOpenSpace, teamMatch, ownShipAlive, spectating";

        private static readonly string[] Names =
            { "onOwnShip", "onAllyShip", "onEnemyShip", "inOpenSpace", "teamMatch", "ownShipAlive", "spectating" };

        /// <summary>Conditions on other ships' state, named so that authoring one is refused with
        /// the reason rather than as a misspelling.</summary>
        private static readonly string[] OtherShipConditions =
        {
            "allyShipHullBelow", "ownShipHullBelow", "enemyShipHullBelow", "enemyShipWithin",
            "allyShipWithin", "allyShipLostRecently", "allyShipLost", "forceShips", "allyShips",
            "enemyShips", "outnumbered",
        };

        /// <summary>Each holder's last received answer, packed (<see cref="Pack"/>).</summary>
        private static readonly Dictionary<int, int> Received = new Dictionary<int, int>();
        private static readonly HashSet<int> SentTo = new HashSet<int>();
        private static readonly HashSet<int> Warned = new HashSet<int>();
        private static float _nextPoll;
        /// <summary>This client's own adopted answer, packed; -1 before the first poll in a room.</summary>
        private static int _adopted = -1;
        private static int _candidate = -1;
        private static string _room;

        /// <summary>Some installed role declares a condition. Nothing is polled or sent
        /// otherwise.</summary>
        internal static bool InUse { get; private set; }

        /// <summary>Turns polling on for an accepted role that needs its holders' answers: a perk
        /// with conditions or on characterAboard, or abilityWhen. Called only once the whole role
        /// has passed validation, so a rejected role starts nothing.</summary>
        internal static void DeclareFor(RoleManifest m)
        {
            if (m == null) return;
            bool needs = !m.AbilityWhen.Empty || (m.hero != null && NeedsAnswer(m.hero.Scope));
            if (m.perks != null)
                foreach (PerkSpec p in m.perks)
                    if (p != null && NeedsAnswer(p.Scope)) needs = true;
            if (needs) InUse = true;
        }

        /// <summary>The perk reads its holder's answer: it has conditions or lands on the ship the
        /// holder is aboard.</summary>
        internal static bool NeedsAnswer(PerkScope scope)
        {
            return scope != null && (!scope.When.Empty || scope.Ships == ShipSet.Aboard);
        }

        /// <summary>This client's own adopted mask; -1 before the first poll.</summary>
        internal static int LocalMask { get { return MaskPart(_adopted); } }

        /// <summary>A holder's mask as this client applies it: its own for the local player, the
        /// last received for anyone else; -1 when none has arrived.</summary>
        internal static int MaskOf(int actor) { return MaskPart(AnswerOf(actor)); }

        /// <summary>The number of the ship a holder's answer says their character is aboard; 0 for
        /// none, or when no answer has arrived.</summary>
        internal static int AboardShipOf(int actor) { return ShipPart(AnswerOf(actor)); }

        private static int AnswerOf(int actor)
        {
            if (actor == EnemyStatuses.LocalActor()) return _adopted;
            int answer;
            return Received.TryGetValue(actor, out answer) ? answer : -1;
        }

        /// <summary>One answer as one int, so the debounce and the change check compare both
        /// parts at once. A ship number out of range packs as 0.</summary>
        internal static int Pack(int mask, int aboardShip)
        {
            if (mask < 0) return -1;
            if (aboardShip < 0 || aboardShip > MaxShipNumber) aboardShip = 0;
            return (mask & AllBits) | (aboardShip << ShipShift);
        }

        /// <summary>The mask of a packed answer; -1 for none.</summary>
        internal static int MaskPart(int answer) { return answer < 0 ? -1 : answer & AllBits; }

        /// <summary>The aboard ship number of a packed answer; 0 for none.</summary>
        internal static int ShipPart(int answer) { return answer < 0 ? 0 : answer >> ShipShift; }

        // ------------------------------------------------------------------ parsing

        /// <summary>The bit for one condition name, case-insensitive; 0 for an unknown name.</summary>
        internal static int BitOf(string name)
        {
            for (int i = 0; i < Names.Length; i++)
                if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return 1 << i;
            return 0;
        }

        /// <summary>The condition names a mask carries, for log lines.</summary>
        internal static string Describe(int mask)
        {
            if (mask < 0) return "unknown";
            List<string> set = new List<string>();
            for (int i = 0; i < Names.Length; i++)
                if ((mask & (1 << i)) != 0) set.Add(Names[i]);
            return set.Count == 0 ? "none" : string.Join(", ", set.ToArray());
        }

        /// <summary>
        /// A manifest condition list: a name, or an array of names, each optionally prefixed with
        /// '!' to require it false; every entry must hold. Null or empty is no condition. Returns
        /// the problem that refuses it, phrased to follow "has", or null.
        /// </summary>
        internal static string Parse(object raw, out ConditionSet set)
        {
            set = new ConditionSet();
            if (raw == null) return null;
            List<JToken> entries = new List<JToken>();
            JToken token = raw as JToken ?? JToken.FromObject(raw);
            if (token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Array) entries.AddRange((JArray)token);
            else entries.Add(token);

            foreach (JToken entry in entries)
            {
                if (entry == null || entry.Type == JTokenType.Null) continue;
                if (entry.Type == JTokenType.Object)
                {
                    foreach (JProperty prop in ((JObject)entry).Properties())
                        if (IsOtherShipCondition(prop.Name)) return OtherShipProblem(prop.Name);
                    return "a condition written as an object (" + entry.ToString(Newtonsoft.Json.Formatting.None) +
                           "); a condition is a name (" + KnownConditions + ")";
                }
                if (entry.Type != JTokenType.String)
                    return "a condition that is not a name (" + entry.ToString(Newtonsoft.Json.Formatting.None) + ")";
                string text = ((string)entry ?? "").Trim();
                if (text.Length == 0) continue;
                bool negate = text.StartsWith("!", StringComparison.Ordinal);
                string name = negate ? text.Substring(1).Trim() : text;
                int bit = BitOf(name);
                if (bit == 0)
                {
                    if (IsOtherShipCondition(name)) return OtherShipProblem(name);
                    return "unrecognised condition '" + text + "' (this framework knows " + KnownConditions +
                           "; prefix one with ! to require it false)";
                }
                if (negate) set.Forbid |= bit; else set.Require |= bit;
                if ((set.Require & set.Forbid) != 0)
                    return "condition '" + name + "' both required and forbidden";
            }
            return null;
        }

        private static bool IsOtherShipCondition(string name)
        {
            foreach (string n in OtherShipConditions)
                if (string.Equals(n, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string OtherShipProblem(string name)
        {
            return "condition '" + name + "', which reads other ships' state; this framework evaluates " +
                   "only conditions the holder's own client decides (" + KnownConditions + ")";
        }

        // ------------------------------------------------------------------ evaluation

        /// <summary>
        /// One player's mask, from <paramref name="view"/> alone. Aboard: the ship whose rooms or
        /// bubble hold the character, of any side. onOwnShip when that is the crew ship, or was
        /// until it started dying; onAllyShip when it is another living ship of their side;
        /// onEnemyShip when it is of another side; inOpenSpace when there is none. Aboard a dying
        /// ship of their side that was not theirs, none of the four is set. Without a character
        /// none of the four is set and spectating is.
        /// </summary>
        internal static int Evaluate(TopologyView view, int actor)
        {
            int mask = 0;
            if (view.TeamMatch) mask |= (int)HolderCondition.TeamMatch;
            bool character = view.HasCharacter(actor);
            if (!character || view.PlacedOnNoShip(actor)) mask |= (int)HolderCondition.Spectating;
            object crew = view.CrewShip(actor);
            if (crew != null && view.Living(crew)) mask |= (int)HolderCondition.OwnShipAlive;
            if (!character) return mask;
            object aboard = view.AboardShip(actor);
            if (aboard == null) mask |= (int)HolderCondition.InOpenSpace;
            else if (view.IsCrewShip(aboard, actor)) mask |= (int)HolderCondition.OnOwnShip;
            else if (view.OnSide(aboard, actor))
            {
                if (view.Living(aboard)) mask |= (int)HolderCondition.OnAllyShip;
            }
            else mask |= (int)HolderCondition.OnEnemyShip;
            return mask;
        }

        /// <summary>One player's answer as their own client reads it: the mask and the number of
        /// the ship their character is aboard, of any side, dying or not (0 for none). Every
        /// receiver applies the side and living checks itself.</summary>
        internal static int Read(TopologyView view, int actor)
        {
            return Pack(Evaluate(view, actor), view.NumberOf(view.AboardShip(actor)));
        }

        /// <summary>
        /// The debounce: a value differing from the adopted one is adopted once two consecutive
        /// polls read it. The first value read is adopted at once. Returns the adopted mask.
        /// </summary>
        internal static int Debounce(int raw, ref int adopted, ref int candidate)
        {
            if (adopted < 0) { adopted = raw; candidate = -1; return adopted; }
            if (raw == adopted) { candidate = -1; return adopted; }
            if (raw == candidate) { adopted = raw; candidate = -1; return adopted; }
            candidate = raw;
            return adopted;
        }

        // ------------------------------------------------------------------ owner side

        /// <summary>Driver step: four times a second while a role declares a condition.</summary>
        internal static void Poll()
        {
            if (!InUse) return;
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;

            Room room = SyncRoom();
            Prune(room);

            int actor = EnemyStatuses.LocalActor();
            int before = _adopted;
            int adopted = Debounce(Read(Topology.View, actor), ref _adopted, ref _candidate);
            if (adopted != before)
            {
                SentTo.Clear();
                CharacterShipRoute.RequestPass();
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: conditions - actor " + actor + " now " + DescribeAnswer(adopted) +
                                       " (this client).");
            }
            SendToNewPeers(adopted);
        }

        /// <summary>
        /// On entering, leaving or changing room, forgets every received answer, every send
        /// record and this client's own adopted answer: none of them belongs to the new room.
        /// Runs at the top of both <see cref="Poll"/> and <see cref="Receive"/>. A peer sends a
        /// joiner its answer as soon as the join is announced, which can be before the joiner's
        /// first poll in the room, and does not send again until its answer changes; the receive
        /// path running this first keeps that answer rather than leaving the poll to wipe it.
        /// </summary>
        private static Room SyncRoom()
        {
            Room room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
            string roomName = room != null ? room.Name : null;
            if (roomName == _room) return room;
            _room = roomName;
            if (Received.Count > 0 || _adopted >= 0) CharacterShipRoute.RequestPass();
            Received.Clear();
            SentTo.Clear();
            _adopted = -1;
            _candidate = -1;
            return room;
        }

        /// <summary>Sends the adopted answer to every peer on this contract that has not had it.</summary>
        private static void SendToNewPeers(int answer)
        {
            if (answer < 0 || !PhotonNetwork.InRoom) return;
            Player[] peers = RuntimeAbilities.Recipients();
            if (peers.Length == 0) return;
            List<Player> to = null;
            foreach (Player p in peers)
                if (p != null && !SentTo.Contains(p.ActorNumber)) (to ?? (to = new List<Player>())).Add(p);
            if (to == null) return;
            if (!EctypeNet.Send(EctypeNet.ConditionMask, to.ToArray(),
                                Message(MaskPart(answer), ShipPart(answer)), reliable: true)) return;
            foreach (Player p in to) SentTo.Add(p.ActorNumber);
        }

        /// <summary>The message: [int mask, int aboardShip].</summary>
        internal static object[] Message(int mask, int aboardShip) { return new object[] { mask, aboardShip }; }

        /// <summary>Drops masks and send records of players no longer in the room.</summary>
        private static void Prune(Room room)
        {
            if (room == null) return;
            List<int> gone = null;
            foreach (int actor in Received.Keys)
                if (room.GetPlayer(actor) == null) (gone ?? (gone = new List<int>())).Add(actor);
            if (gone != null)
            {
                foreach (int actor in gone) Received.Remove(actor);
                CharacterShipRoute.RequestPass();
            }
            SentTo.RemoveWhere(actor => room.GetPlayer(actor) == null);
        }

        // ------------------------------------------------------------------ receiving side

        /// <summary>Receiver of <see cref="EctypeNet.ConditionMask"/>: [int mask, int aboardShip].
        /// The sender is the holder: a client publishes only its own player's answer.</summary>
        internal static void Receive(int sender, object[] args)
        {
            SyncRoom();
            int mask = (int)args[0];
            int aboardShip = (int)args[1];
            if (sender == EnemyStatuses.LocalActor()) return;
            if ((mask & ~AllBits) != 0 || mask < 0 || aboardShip < 0 || aboardShip > MaxShipNumber)
            {
                if (Warned.Add(sender))
                    Plugin.Log.LogWarning("Ectype: ignored a condition answer (mask " + mask + ", aboard ship " +
                                          aboardShip + ") from actor " + sender + ": it carries values this " +
                                          "network contract does not define (reported once per sender).");
                return;
            }
            Apply(sender, Pack(mask, aboardShip));
        }

        /// <summary>Records a holder's packed answer and rebuilds on a change.</summary>
        internal static void Apply(int actor, int answer)
        {
            if (!Record(actor, answer)) return;
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: conditions - actor " + actor + " now " + DescribeAnswer(answer) + ".");
        }

        /// <summary>The conditions an answer carries, then the aboard ship when it names one.</summary>
        internal static string DescribeAnswer(int answer)
        {
            int ship = ShipPart(answer);
            return Describe(MaskPart(answer)) + (ship > 0 ? " (aboard ship " + ship + ")" : "");
        }

        /// <summary>Stores a holder's packed answer and asks the route for a pass; false when it is
        /// unchanged.</summary>
        internal static bool Record(int actor, int answer)
        {
            int old;
            if (Received.TryGetValue(actor, out old) && old == answer) return false;
            Received[actor] = answer;
            CharacterShipRoute.RequestPass();
            return true;
        }

#if ECTYPE_DEV
        /// <summary>Dev build: forgets a holder's mask a self-test recorded.</summary>
        internal static void Forget(int actor) { Received.Remove(actor); }

        /// <summary>Dev build: a holder's received packed answer, without the local-player lookup
        /// (which touches Photon); -1 when none.</summary>
        internal static int ReceivedAnswer(int actor)
        {
            int answer;
            return Received.TryGetValue(actor, out answer) ? answer : -1;
        }

        /// <summary>Dev build: restores the declared flag a self-test's parsing set.</summary>
        internal static void SetInUse(bool inUse) { InUse = inUse; }
#endif
    }
}
