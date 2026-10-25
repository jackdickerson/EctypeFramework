using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Game.Player;
using CG.Ship.Object;
using CG.Space;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

using PhotonPlayer = Photon.Realtime.Player;

namespace Ectype
{
    /// <summary>Why a <see cref="CharacterStatuses.Request"/> or
    /// <see cref="CharacterStatuses.TryRemove"/> did nothing, and why a character's client
    /// refused a received request or announcement.</summary>
    public enum CharacterStatusRefusal
    {
        /// <summary>Not refused: applied, or removed.</summary>
        None = 0,
        /// <summary>The target is null, has no stat collection or no PhotonView, or a received
        /// request names a character the receiving client does not own.</summary>
        InvalidTarget = 1,
        /// <summary>No template is registered under the key on the judging client.</summary>
        NotRegistered = 2,
        /// <summary>The framework's patches did not apply this session.</summary>
        Inert = 3,
        /// <summary>The duration is not finite or is below the template's MinSeconds.</summary>
        InvalidDuration = 4,
        /// <summary>The magnitude is 0 or below, or not finite.</summary>
        InvalidMagnitude = 5,
        /// <summary>A request for a template that does not set AcceptsRequests.</summary>
        NotAccepted = 6,
        /// <summary>The target's client does not run this framework on this network contract,
        /// so it has nothing to receive a request with.</summary>
        NoPeer = 7,
        /// <summary>The target character is dead.</summary>
        TargetDead = 8,
        /// <summary>The requester has no character on the judging client.</summary>
        SenderMissing = 9,
        /// <summary>The requester's character is dead.</summary>
        SenderDead = 10,
        /// <summary>RefuseSeated is set and the requester is seated.</summary>
        SenderSeated = 11,
        /// <summary>RefuseInTube is set and the requester or the target is in a clone tube.</summary>
        InCloneTube = 12,
        /// <summary>The requester's relation to the target fails the template's Stance.</summary>
        Stance = 13,
        /// <summary>The requester's character is farther from the target than the range allows.</summary>
        OutOfRange = 14,
        /// <summary>RequiresLineOfSight is set and something blocks the line between the two
        /// characters.</summary>
        NoLineOfSight = 15,
        /// <summary>The requester sent this key faster than MinInterval allows beyond its burst,
        /// or sent more than the per-requester cap across all keys.</summary>
        RateLimited = 16,
        /// <summary>A removal of, or an announcement for, a status on a character the sender
        /// does not own.</summary>
        Authority = 17,
        /// <summary>An announcement for a template that does not set Presented on this client.</summary>
        NotPresented = 18,
        /// <summary>A sent request whose target's client did not answer in time.</summary>
        NoAnswer = 19,
        /// <summary>The call threw, or a message was malformed.</summary>
        Failed = 20,
        /// <summary>Photon refused to send the request (not connected?).</summary>
        SendFailed = 21,
    }

    /// <summary>
    /// One <see cref="CharacterStatuses.Request"/>. A request this client applied itself or
    /// refused is complete when Request returns; a sent one completes when the target's client
    /// answers, or with <see cref="CharacterStatusRefusal.NoAnswer"/> when it does not.
    /// </summary>
    public sealed class CharacterStatusRequest
    {
        private Action<CharacterStatusRequest> _completed;

        internal int Id;
        internal int TargetActor;
        internal float Deadline;

        /// <summary>The character the status was requested on.</summary>
        public Player Target { get; private set; }

        /// <summary>The status key.</summary>
        public string Key { get; private set; }

        /// <summary>The request left this client for the target's client.</summary>
        public bool Sent { get; private set; }

        /// <summary>The verdict is in: <see cref="Refusal"/> holds it.</summary>
        public bool Done { get; private set; }

        /// <summary><see cref="CharacterStatusRefusal.None"/> while pending and when applied;
        /// otherwise why the status was not applied.</summary>
        public CharacterStatusRefusal Refusal { get; private set; }

        /// <summary>Done, and the status was applied on the target's client.</summary>
        public bool Applied { get { return Done && Refusal == CharacterStatusRefusal.None; } }

        internal CharacterStatusRequest(Player target, string key)
        {
            Target = target;
            Key = key;
        }

        /// <summary>Runs the callback once, when the verdict is in: at once when it already is.
        /// Returns this request, so it chains onto Request.</summary>
        public CharacterStatusRequest OnCompleted(Action<CharacterStatusRequest> callback)
        {
            if (callback == null) return this;
            if (Done) Invoke(callback);
            else _completed += callback;
            return this;
        }

        internal void MarkSent() { Sent = true; }

        internal void Complete(CharacterStatusRefusal refusal)
        {
            if (Done) return;
            Done = true;
            Refusal = refusal;
            Action<CharacterStatusRequest> cb = _completed;
            _completed = null;
            if (cb != null) Invoke(cb);
        }

        private void Invoke(Action<CharacterStatusRequest> cb)
        {
            try { cb(this); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: character status '" + Key + "' request callback threw: " + e);
            }
        }
    }

    /// <summary>What the judging client sees of a request: the target's and the requester's
    /// characters, as this client simulates them. Gathered from the game, or built by hand in
    /// the dev self-test.</summary>
    internal struct CharacterRequestFacts
    {
        internal bool VictimAlive;
        internal bool VictimInTube;
        internal bool SourceKnown;
        internal bool SourceAlive;
        internal bool SourceSeated;
        internal bool SourceInTube;
        internal bool Hostile;
        internal float Distance;
        internal bool LineClear;
    }

    /// <summary>
    /// A token bucket per id: <c>burst</c> tokens to start with, one more every
    /// <c>interval</c> seconds up to <c>burst</c>, one taken per accepted request. A refused
    /// request takes nothing.
    /// </summary>
    internal sealed class CharacterStatusRate
    {
        private struct Bucket
        {
            public float Tokens;
            public float At;
        }

        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();

        /// <summary>True, and one token taken, when the id has a token at <paramref name="now"/>.
        /// Always true for an interval of 0.</summary>
        internal bool TryTake(string id, int burst, float interval, float now)
        {
            if (!(interval > 0f)) return true;
            float cap = Math.Max(1, burst);
            Bucket b;
            float tokens = cap;
            if (_buckets.TryGetValue(id, out b))
                tokens = Math.Min(cap, b.Tokens + Math.Max(0f, now - b.At) / interval);
            bool fits = tokens >= 1f;
            _buckets[id] = new Bucket { Tokens = fits ? tokens - 1f : tokens, At = now };
            return fits;
        }

        internal void Forget(string id) { _buckets.Remove(id); }
    }

    /// <summary>
    /// Character statuses across clients. The client that owns a character is the only one
    /// whose copy of it the game reads for damage and movement, so it is the only client a
    /// status on that character can live on.
    ///
    /// Request: the requester sends (request id, target actor, key, duration, magnitude,
    /// attacker view) to the target's client alone. That client judges it against its own view
    /// of both characters - never against anything the message claims about position or
    /// state - applies it exactly as a local Apply would, or refuses it, and answers the
    /// requester with the verdict. The requester runs the same checks first, from its own view
    /// with a looser range, and sends nothing a check refuses.
    ///
    /// Presentation: when the template is Presented, the owning client announces each
    /// application (target actor, key, end on the server clock, magnitude) to the peers the
    /// template's Visibility allows. Each runs a presentation copy - the CreateVfx visual and
    /// IsPresented - which expires on the announced end with no further message. An end
    /// before that (removal, death, despawn) is announced to the same peers. Only the
    /// character's own client may announce: an announcement naming another actor's character
    /// is refused.
    /// </summary>
    internal static class CharacterStatusSync
    {
        /// <summary>Height above a character's feet, along its up axis, that line of sight is
        /// cast between.</summary>
        internal const float ChestHeight = 1.2f;

        /// <summary>A re-announcement whose end differs from the last by less than this goes
        /// only to peers that were not told yet.</summary>
        internal const int AnnounceSlackMs = 250;

        /// <summary>Seconds between two announcements of one status; a later one waits.</summary>
        internal const float AnnounceGapSeconds = 0.5f;

        /// <summary>Seconds a sent request waits for the target's answer.</summary>
        internal const float AnswerTimeoutSeconds = 10f;

        /// <summary>Longest key a received message may carry.</summary>
        internal const int MaxKeyLength = 128;

        /// <summary>Requests one requester may send across all keys: a burst of this many,
        /// refilled at this many per second.</summary>
        internal const int SenderBurst = 20;

        /// <summary>Requests waiting for an answer on this client.</summary>
        internal const int MaxPending = 256;

        /// <summary>Requests to one target waiting for an answer; past this the oldest completes
        /// with NoAnswer.</summary>
        internal const int MaxPendingPerTarget = 40;

        /// <summary>The requesting client's range is MaxRange times this, or MaxRange plus
        /// <see cref="RangeSlackMetres"/>, whichever is larger.</summary>
        internal const float RangeSlackFactor = 1.15f;
        internal const float RangeSlackMetres = 1f;

        /// <summary>Longest a received announcement may keep a copy, in milliseconds.</summary>
        internal const int MaxCopyMs = 3600 * 1000;

        /// <summary>Accepted requests on the target's client, per requester and key, and per
        /// requester across keys.</summary>
        internal static readonly CharacterStatusRate Accepted = new CharacterStatusRate();

        /// <summary>Sent requests on the requester's client, per target and key.</summary>
        internal static readonly CharacterStatusRate Sent = new CharacterStatusRate();

        /// <summary>All requests per requester on the target's client (a burst of
        /// <see cref="SenderBurst"/> + 1, then <see cref="SenderBurst"/> a second), all requests
        /// per target on the requester's client (<see cref="SenderBurst"/>, the same rate), and
        /// all announcements per announcer on a receiving client. Kept apart from the per-key
        /// buckets so no key can share their ids.</summary>
        internal static readonly CharacterStatusRate Flood = new CharacterStatusRate();
        internal static readonly CharacterStatusRate SentFlood = new CharacterStatusRate();
        internal static readonly CharacterStatusRate AnnounceFlood = new CharacterStatusRate();

        private static readonly Dictionary<int, CharacterStatusRequest> Pending =
            new Dictionary<int, CharacterStatusRequest>();
        private static readonly Dictionary<int, Queue<int>> PendingByTarget =
            new Dictionary<int, Queue<int>>();
        private static readonly List<int> PendingScratch = new List<int>();
        private static int _nextRequestId;

        private static int _sightMask = -1;

        // ---------------------------------------------------------------- registration

        private static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        /// <summary>Null when the template's request fields are usable, else what is wrong.</summary>
        internal static string Validate(CharacterStatusDef def)
        {
            if (def.Burst < 1)
                return "Burst is " + def.Burst + "; it must be 1 or more";
            if (!Finite(def.MinInterval) || def.MinInterval < 0f)
                return "MinInterval is " + def.MinInterval + "; it must be a finite number of seconds, 0 or more";
            if (!Finite(def.MaxRange) || def.MaxRange < 0f)
                return "MaxRange is " + def.MaxRange + "; it must be a finite number of metres, 0 or more";
            if (!Finite(def.MaxSeconds) || def.MaxSeconds <= 0f)
                return "MaxSeconds is " + def.MaxSeconds + "; it must be a finite number of seconds above 0";
            if (!Finite(def.MinSeconds) || def.MinSeconds < 0f || def.MinSeconds > def.MaxSeconds)
                return "MinSeconds is " + def.MinSeconds + "; it must be a finite number of seconds from 0 to MaxSeconds";
            if (!Finite(def.MaxMagnitude) || def.MaxMagnitude <= 0f)
                return "MaxMagnitude is " + def.MaxMagnitude + "; it must be a finite number above 0";
            return null;
        }

        /// <summary>One warning per field group a template sets without the switch that uses it.</summary>
        internal static void WarnUnusedFields(CharacterStatusDef def)
        {
            if (!def.AcceptsRequests && (def.Stance != StatusStance.Any || def.MaxRange > 0f ||
                                         def.RequiresLineOfSight || def.MinInterval > 0f ||
                                         def.RefuseSeated || def.RefuseInTube))
                Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' sets request " +
                                      "checks (Stance, MaxRange, RequiresLineOfSight, RefuseSeated, " +
                                      "RefuseInTube or MinInterval) but not AcceptsRequests, so it " +
                                      "accepts no requests for them to judge.");
            if (!def.Presented && (def.Visibility != StatusVisibility.All || def.CreateVfx != null))
                Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' sets Visibility " +
                                      "or CreateVfx but is not Presented, so no presentation copy is sent.");
        }

        /// <summary>A template that sets Replicated, which changes nothing.</summary>
        internal static void WarnReplicated(CharacterStatusDef def)
        {
            if (!CharacterStatuses.WarnOnce("replicated:" + def.Key)) return;
            Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' sets Replicated, which " +
                                  "changes nothing: a status runs on the client that owns the character. " +
                                  "AcceptsRequests lets other clients request it, and Presented shows " +
                                  "it to them.");
        }

        /// <summary>Apply on another player's character: it lands on this client's copy,
        /// which none of that player's damage or movement reads.</summary>
        internal static void WarnNotOwned(CharacterStatusDef def, Player target)
        {
            if (!CharacterStatuses.WarnOnce("notmine:" + def.Key)) return;
            Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' applied to " +
                                  target.name + ", a character this client does not own. It " +
                                  "runs on this client's copy only and changes nothing for " +
                                  "that player; " + (def.AcceptsRequests
                                      ? "use CharacterStatuses.Request to ask that player's client."
                                      : "set AcceptsRequests on the template and use " +
                                        "CharacterStatuses.Request to ask that player's client."));
        }

        /// <summary>This client's side, -1 when unknown.</summary>
        internal static int LocalSide()
        {
            return Topology.ForceOf(PhotonNetwork.LocalPlayer);
        }

        // ---------------------------------------------------------------- judging

        /// <summary>Duration and magnitude: refused when unusable, cut to the template's
        /// caps when above them.</summary>
        internal static CharacterStatusRefusal Bound(CharacterStatusDef def, ref float seconds,
                                                     ref float magnitude)
        {
            if (!Finite(seconds) || !(seconds > 0f) || seconds < def.MinSeconds)
                return CharacterStatusRefusal.InvalidDuration;
            if (!Finite(magnitude) || !(magnitude > 0f)) return CharacterStatusRefusal.InvalidMagnitude;
            if (seconds > def.MaxSeconds) seconds = def.MaxSeconds;
            if (magnitude > def.MaxMagnitude) magnitude = def.MaxMagnitude;
            return CharacterStatusRefusal.None;
        }

        /// <summary>The distance a request may span: MaxRange on the target's client; on the
        /// requesting client, the larger of MaxRange times <see cref="RangeSlackFactor"/> and
        /// MaxRange plus <see cref="RangeSlackMetres"/>, so a request at the edge reaches the
        /// target's client to be judged there. 0 when the template checks no range.</summary>
        internal static float RangeLimit(CharacterStatusDef def, bool requesterView)
        {
            if (!(def.MaxRange > 0f)) return 0f;
            if (!requesterView) return def.MaxRange;
            return Math.Max(def.MaxRange * RangeSlackFactor, def.MaxRange + RangeSlackMetres);
        }

        /// <summary>
        /// Every check but rate, in order. Pure: the facts are whatever the caller saw, so the
        /// requester and the target's client run the same function on their own views.
        /// </summary>
        internal static CharacterStatusRefusal Judge(CharacterStatusDef def, ref float seconds,
                                                     ref float magnitude, CharacterRequestFacts f,
                                                     bool requesterView)
        {
            if (def == null) return CharacterStatusRefusal.NotRegistered;
            if (!def.AcceptsRequests) return CharacterStatusRefusal.NotAccepted;
            CharacterStatusRefusal bounds = Bound(def, ref seconds, ref magnitude);
            if (bounds != CharacterStatusRefusal.None) return bounds;
            if (!f.VictimAlive) return CharacterStatusRefusal.TargetDead;
            if (!f.SourceKnown) return CharacterStatusRefusal.SenderMissing;
            if (!f.SourceAlive) return CharacterStatusRefusal.SenderDead;
            if (def.RefuseSeated && f.SourceSeated) return CharacterStatusRefusal.SenderSeated;
            if (def.RefuseInTube && (f.SourceInTube || f.VictimInTube)) return CharacterStatusRefusal.InCloneTube;
            if (def.Stance == StatusStance.Hostile && !f.Hostile) return CharacterStatusRefusal.Stance;
            if (def.Stance == StatusStance.Friendly && f.Hostile) return CharacterStatusRefusal.Stance;
            float limit = RangeLimit(def, requesterView);
            if (limit > 0f && !(f.Distance <= limit)) return CharacterStatusRefusal.OutOfRange;
            if (def.RequiresLineOfSight && !f.LineClear) return CharacterStatusRefusal.NoLineOfSight;
            return CharacterStatusRefusal.None;
        }

        /// <summary>What this client sees of the two characters. The seat, tube and line of
        /// sight are read only when the template asks for them.</summary>
        internal static CharacterRequestFacts Gather(Player source, Player victim, CharacterStatusDef def)
        {
            CharacterRequestFacts f = new CharacterRequestFacts();
            f.VictimAlive = Alive(victim);
            f.SourceKnown = source != null;
            if (source == null || victim == null) return f;
            f.SourceAlive = Alive(source);
            f.Hostile = Topology.Hostile(source, victim);
            f.Distance = Vector3.Distance(source.transform.position, victim.transform.position);
            if (def != null && def.RefuseSeated) f.SourceSeated = Seated(source);
            if (def != null && def.RefuseInTube)
            {
                f.SourceInTube = InTube(source);
                f.VictimInTube = InTube(victim);
            }
            f.LineClear = def == null || !def.RequiresLineOfSight || LineClear(source, victim);
            return f;
        }

        /// <summary>The game's replicated life state: NetworkNotifyDeath sets it to Dead on
        /// every client and the respawn sets it back, where the health component's own flag is
        /// set only on the owning client. A client that joined after a death learns it from
        /// that character's first health update, which marks the character dead at 0.</summary>
        internal static bool Alive(Player p)
        {
            return p != null && p.State == LifeState.Alive;
        }

        /// <summary>The seat ability (chairs, the helm, turrets) is active; its state is
        /// synchronised to every client.</summary>
        internal static bool Seated(Player p)
        {
            try
            {
                if (p == null || p.Locomotion == null) return false;
                SittingInChair seat = p.Locomotion.GetAbility<SittingInChair>();
                return seat != null && seat.IsActive;
            }
            catch (Exception) { return false; }
        }

        private static readonly FieldInfo CloneTubesField = AccessTools.Field(typeof(SpawnSystem), "clonetubes");

        /// <summary>A clone tube holds the character as its occupant, or, on the client that owns
        /// it, its in-tube ability is active.</summary>
        internal static bool InTube(Player p)
        {
            try
            {
                if (p == null) return false;
                if (p.photonView != null && p.photonView.IsMine && p.Locomotion != null)
                {
                    InClonetube inTube = p.Locomotion.GetAbility<InClonetube>();
                    if (inTube != null && inTube.IsActive) return true;
                }
                SpawnSystem spawn = SpawnSystem.Instance;
                if (spawn == null || CloneTubesField == null) return false;
                List<CloneTube> tubes = CloneTubesField.GetValue(spawn) as List<CloneTube>;
                if (tubes == null) return false;
                foreach (CloneTube tube in tubes)
                    if (tube != null && tube.GetOccupant() == p) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>Chest to chest against level geometry and space objects, triggers ignored.</summary>
        internal static bool LineClear(Player source, Player victim)
        {
            int mask = SightMask();
            if (mask == 0) return false;
            Vector3 a = source.transform.position + source.transform.up * ChestHeight;
            Vector3 b = victim.transform.position + victim.transform.up * ChestHeight;
            return !Physics.Linecast(a, b, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// The layers that stop a line of sight: StaticPlatform and MovingPlatform (hulls and
        /// level geometry) and SpaceObjects (asteroids, NPC hulls, wrecks). Interactive objects
        /// are left out so a console or a carried item does not hide a player standing behind
        /// it. Characters sit on their own layer, so neither end of the line hits its own body.
        /// Resolved by name once; a game without the layers fails every line-of-sight check
        /// closed.
        /// </summary>
        internal static int SightMask()
        {
            if (_sightMask >= 0) return _sightMask;
            _sightMask = LayerMask.GetMask("StaticPlatform", "MovingPlatform", "SpaceObjects");
            if (_sightMask == 0)
                Plugin.Log.LogError("Ectype: the game has no 'StaticPlatform', 'MovingPlatform' or " +
                                    "'SpaceObjects' layer; every character status that requires line " +
                                    "of sight is refused until the framework is updated for this game version.");
            return _sightMask;
        }

        /// <summary>A refusal in words, the same on the requester and on the target's client.
        /// <paramref name="seconds"/> and <paramref name="magnitude"/> are the values as received.</summary>
        internal static string Reason(CharacterStatusRefusal r, CharacterStatusDef def, CharacterRequestFacts f,
                                      float seconds, float magnitude, bool requesterView)
        {
            switch (r)
            {
                case CharacterStatusRefusal.NotAccepted:
                    return "the template does not set AcceptsRequests, so it accepts no requests";
                case CharacterStatusRefusal.InvalidDuration:
                    return "duration " + seconds + " s is not a finite number of at least MinSeconds " +
                           def.MinSeconds + " s";
                case CharacterStatusRefusal.InvalidMagnitude:
                    return "magnitude " + magnitude + " is not a finite number above 0";
                case CharacterStatusRefusal.TargetDead:
                    return "the target character is dead";
                case CharacterStatusRefusal.SenderMissing:
                    return "the requester has no character on this client";
                case CharacterStatusRefusal.SenderDead:
                    return "the requester's character is dead";
                case CharacterStatusRefusal.SenderSeated:
                    return "the requester is seated";
                case CharacterStatusRefusal.InCloneTube:
                    return (f.SourceInTube ? "the requester" : "the target") + " is in a clone tube";
                case CharacterStatusRefusal.Stance:
                    return "stance " + def.Stance + " and the requester is " +
                           (f.Hostile ? "on another side from the target" : "on the target's side");
                case CharacterStatusRefusal.OutOfRange:
                    return "the requester is " + f.Distance.ToString("0.0") + " m from the target, beyond " +
                           RangeLimit(def, requesterView).ToString("0.0") + " m";
                case CharacterStatusRefusal.NoLineOfSight:
                    return "something blocks the line between the requester and the target";
                case CharacterStatusRefusal.RateLimited:
                    return "more than " + (requesterView ? def.Burst : def.Burst + 1) +
                           " request(s) at once, or more often than one per " + def.MinInterval + " s";
                case CharacterStatusRefusal.NoPeer:
                    return "the target's client does not run this framework on network contract " +
                           EctypePluginInfo.NET_CONTRACT_VERSION;
                default:
                    return r.ToString();
            }
        }

        // ---------------------------------------------------------------- requesting

        /// <summary>The request half of <see cref="CharacterStatuses.Request"/>, after the
        /// template lookup. Completes the handle unless the request was sent.</summary>
        internal static void Request(CharacterStatusRequest handle, CharacterStatusDef def, float seconds,
                                     float magnitude, OrbitObject attacker)
        {
            Player target = handle.Target;
            if (target.photonView == null)
            {
                handle.Complete(CharacterStatusRefusal.InvalidTarget);
                return;
            }
            if (target.photonView.IsMine)
            {
                if (!Finite(seconds) || !(seconds > 0f))
                {
                    Plugin.Log.LogWarning("Ectype: Request('" + def.Key + "') with duration " +
                                          seconds + "s ignored.");
                    handle.Complete(CharacterStatusRefusal.InvalidDuration);
                    return;
                }
                if (!Alive(target))
                {
                    handle.Complete(CharacterStatusRefusal.TargetDead);
                    return;
                }
                CharacterStatuses.ApplyLocal(target, def, seconds, magnitude, 0, attacker, LocalSide(), false);
                handle.Complete(CharacterStatusRefusal.None);
                return;
            }

            int targetActor = Topology.ActorOf(target);
            PhotonPlayer owner = PlayerOf(targetActor);
            Player me = Topology.CharacterOf(EnemyStatuses.LocalActor());
            CharacterRequestFacts f = Gather(me, target, def);
            // Judged as it will travel, in whole milliseconds, so a duration just above MinSeconds
            // cannot pass here and fail on the target.
            float s = Finite(seconds) ? (float)(Math.Round(seconds * 1000.0) / 1000.0) : seconds;
            float m = magnitude;
            CharacterStatusRefusal r = Judge(def, ref s, ref m, f, true);
            if (r == CharacterStatusRefusal.None && (owner == null || !PeerPresence.HasFramework(owner)))
                r = CharacterStatusRefusal.NoPeer;
            if (r == CharacterStatusRefusal.None && Pending.Count >= MaxPending)
            {
                if (CharacterStatuses.WarnOnce("pending-full"))
                    Plugin.Log.LogWarning("Ectype: " + MaxPending + " character status requests are " +
                                          "waiting for an answer; further requests are refused RateLimited " +
                                          "until some are answered or time out (reported once).");
                r = CharacterStatusRefusal.RateLimited;
            }
            if (r == CharacterStatusRefusal.None &&
                !Sent.TryTake(targetActor + "/" + def.Key, def.Burst, def.MinInterval, Time.unscaledTime))
                r = CharacterStatusRefusal.RateLimited;
            // The target's cap across keys, kept here too, so nothing is sent that it would drop.
            if (r == CharacterStatusRefusal.None &&
                !SentFlood.TryTake(targetActor.ToString(), SenderBurst, 1f / SenderBurst, Time.unscaledTime))
                r = CharacterStatusRefusal.RateLimited;
            if (r != CharacterStatusRefusal.None)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: Request('" + def.Key + "') on " + target.name +
                                       " refused: " + Reason(r, def, f, seconds, magnitude, true) + ".");
                handle.Complete(r);
                return;
            }

            int id = unchecked(++_nextRequestId);
            if (id == 0) id = unchecked(++_nextRequestId);
            int attackerView = attacker != null && attacker.photonView != null ? attacker.photonView.ViewID : 0;
            if (!EctypeNet.Send(EctypeNet.CharacterStatusRequest, new[] { owner },
                                RequestArgs(id, targetActor, def.Key, s, m, attackerView), reliable: true))
            {
                handle.Complete(CharacterStatusRefusal.SendFailed);
                return;
            }
            // Answers are dispatched on the main thread after this returns, so registering
            // after the send cannot miss one.
            handle.Id = id;
            handle.TargetActor = targetActor;
            handle.Deadline = Time.unscaledTime + AnswerTimeoutSeconds;
            handle.MarkSent();
            Pending[id] = handle;
            TrackPerTarget(handle);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: character status '" + def.Key + "' requested on " +
                                   target.name + " (actor " + targetActor + ", request " + id + ", " + s +
                                   " s, magnitude " + m + ").");
        }

        /// <summary>[int requestId, int targetActor, string key, int durationMs, float magnitude,
        /// int attackerView].</summary>
        internal static object[] RequestArgs(int requestId, int targetActor, string key, float seconds,
                                             float magnitude, int attackerView)
        {
            int ms = (int)Math.Min(Math.Round(seconds * 1000.0), int.MaxValue);
            return new object[] { requestId, targetActor, key, Math.Max(ms, 1), magnitude, attackerView };
        }

        /// <summary>[int requestId, int refusal]: the target's verdict on a request.</summary>
        internal static object[] ResultArgs(int requestId, CharacterStatusRefusal refusal)
        {
            return new object[] { requestId, (int)refusal };
        }

        /// <summary>[int targetActor, string key, int endTs, float magnitude].</summary>
        internal static object[] AppliedArgs(int targetActor, string key, int endTs, float magnitude)
        {
            return new object[] { targetActor, key, endTs, magnitude };
        }

        /// <summary>[int targetActor, string key]: the status ended before its announced end.</summary>
        internal static object[] EndedArgs(int targetActor, string key)
        {
            return new object[] { targetActor, key };
        }

        private static PhotonPlayer PlayerOf(int actor)
        {
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            return room != null ? room.GetPlayer(actor) : null;
        }

        /// <summary>Keeps at most <see cref="MaxPendingPerTarget"/> requests to one target
        /// waiting; the oldest beyond that completes with NoAnswer. The queue may hold ids already
        /// answered, which are skipped, so it never grows past the cap.</summary>
        private static void TrackPerTarget(CharacterStatusRequest h)
        {
            Queue<int> q;
            if (!PendingByTarget.TryGetValue(h.TargetActor, out q))
                PendingByTarget[h.TargetActor] = q = new Queue<int>();
            q.Enqueue(h.Id);
            while (q.Count > MaxPendingPerTarget)
            {
                int old = q.Dequeue();
                CharacterStatusRequest oh;
                if (!Pending.TryGetValue(old, out oh)) continue;
                Pending.Remove(old);
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: character status '" + oh.Key + "' request " + old +
                                       " to actor " + oh.TargetActor + " given up: " + MaxPendingPerTarget +
                                       " newer requests to that player are waiting.");
                oh.Complete(CharacterStatusRefusal.NoAnswer);
            }
        }

        /// <summary>Completes sent requests whose answer is overdue.</summary>
        internal static void PollPending()
        {
            if (Pending.Count == 0) return;
            float now = Time.unscaledTime;
            PendingScratch.Clear();
            foreach (KeyValuePair<int, CharacterStatusRequest> kv in Pending)
                if (now >= kv.Value.Deadline) PendingScratch.Add(kv.Key);
            for (int i = 0; i < PendingScratch.Count; i++)
            {
                CharacterStatusRequest h;
                if (!Pending.TryGetValue(PendingScratch[i], out h)) continue;
                Pending.Remove(PendingScratch[i]);
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: character status '" + h.Key + "' request " + h.Id +
                                       " to actor " + h.TargetActor + " got no answer in " +
                                       AnswerTimeoutSeconds + " s.");
                h.Complete(CharacterStatusRefusal.NoAnswer);
            }
        }

        /// <summary>
        /// The target's verdict on one of this client's requests (subtype
        /// <see cref="EctypeNet.CharacterStatusResult"/>). Accepted only from the actor the
        /// request went to.
        /// </summary>
        internal static CharacterStatusRefusal HandleResult(int sender, object[] args)
        {
            if (!EctypeNet.MatchesLayout(EctypeNet.CharacterStatusResult, args))
                return CharacterStatusRefusal.Failed;
            int id = (int)args[0];
            int code = (int)args[1];
            CharacterStatusRequest h;
            if (!Pending.TryGetValue(id, out h))
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: character status answer for request " + id + " from actor " +
                                       sender + " ignored: no such request is waiting (timed out?).");
                return CharacterStatusRefusal.InvalidTarget;
            }
            if (sender != h.TargetActor)
            {
                StatusRefusalLog.Report(sender, "character answer", "*",
                                        "Ectype: ignored a character status answer from actor " + sender +
                                        ": request " + id + " went to actor " + h.TargetActor + ".");
                return CharacterStatusRefusal.Authority;
            }
            Pending.Remove(id);
            CharacterStatusRefusal verdict = Enum.IsDefined(typeof(CharacterStatusRefusal), code)
                                                 ? (CharacterStatusRefusal)code : CharacterStatusRefusal.Failed;
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: character status '" + h.Key + "' request " + id + " to actor " +
                                   sender + " answered " + verdict + ".");
            h.Complete(verdict);
            return verdict;
        }

        // ---------------------------------------------------------------- receiving a request

        /// <summary>
        /// One received request (subtype <see cref="EctypeNet.CharacterStatusRequest"/>).
        /// <paramref name="sender"/> is the actor Photon stamped on the event, the one fact
        /// about the requester the message cannot forge. Every request within the
        /// per-requester cap is answered with its verdict. Returns the verdict, for the dev
        /// self-test; a refusal is logged here.
        /// </summary>
        internal static CharacterStatusRefusal HandleRequest(int sender, object[] args)
        {
            if (!EctypeNet.MatchesLayout(EctypeNet.CharacterStatusRequest, args))
                return CharacterStatusRefusal.Failed;
            int requestId = (int)args[0];
            // One token above the requester's own cap, for requests the network bunched up.
            if (!Flood.TryTake(sender.ToString(), SenderBurst + 1, 1f / SenderBurst, Time.unscaledTime))
            {
                StatusRefusalLog.Report(sender, "character request flood", "*",
                                        "Ectype: refused character status requests from actor " + sender +
                                        ": more than a burst of " + (SenderBurst + 1) + ", then " + SenderBurst +
                                        " a second, across all keys; they are dropped unanswered.");
                return CharacterStatusRefusal.RateLimited;
            }
            CharacterStatusRefusal r = Decide(sender, args);
            if (sender != EnemyStatuses.LocalActor())
            {
                PhotonPlayer to = PlayerOf(sender);
                if (to != null)
                    EctypeNet.Send(EctypeNet.CharacterStatusResult, new[] { to }, ResultArgs(requestId, r),
                                   reliable: true);
            }
            return r;
        }

        private static CharacterStatusRefusal Decide(int sender, object[] args)
        {
            int targetActor = (int)args[1];
            string key = (string)args[2];
            int durationMs = (int)args[3];
            float magnitude = (float)args[4];
            int attackerView = (int)args[5];

            if (key.Length > MaxKeyLength)
            {
                StatusRefusalLog.Report(sender, "character request key", "*",
                                        "Ectype: refused a character status request from actor " + sender +
                                        ": its key is longer than " + MaxKeyLength + " characters.");
                return CharacterStatusRefusal.Failed;
            }
            int me = EnemyStatuses.LocalActor();
            Player victim = targetActor == me ? Topology.CharacterOf(me) : null;
            if (victim == null || victim.photonView == null || !victim.photonView.IsMine)
            {
                StatusRefusalLog.Report(sender, "character request target", "*",
                                        "Ectype: refused character status '" + key + "' request from actor " +
                                        sender + ": " + (targetActor != me
                                            ? "it names actor " + targetActor + ", not this client"
                                            : "this client has no character of its own") + ".");
                return CharacterStatusRefusal.InvalidTarget;
            }
            if (!Plugin.PatchesApplied) return CharacterStatusRefusal.Inert;
            CharacterStatusDef def = CharacterStatuses.Lookup(key);
            if (def == null)
            {
                StatusRefusalLog.Report(sender, "character unknown key", "*",
                                        "Ectype: peer requested unknown character status '" + key +
                                        "'; ignoring (mod not installed here?).");
                return CharacterStatusRefusal.NotRegistered;
            }

            Player source = Topology.CharacterOf(sender);
            CharacterRequestFacts f = Gather(source, victim, def);
            float seconds = durationMs / 1000f, rawSeconds = seconds, rawMagnitude = magnitude;
            CharacterStatusRefusal r = Judge(def, ref seconds, ref magnitude, f, false);
            // One token more than the requester keeps: requests the network delivered closer
            // together than they were sent still land, and the sustained rate stays MinInterval.
            if (r == CharacterStatusRefusal.None &&
                !Accepted.TryTake(sender + "/" + key, def.Burst + 1, def.MinInterval, Time.unscaledTime))
                r = CharacterStatusRefusal.RateLimited;
            if (r != CharacterStatusRefusal.None)
            {
                StatusRefusalLog.Report(sender, "character request", key,
                                        "Ectype: refused character status '" + key + "' request from actor " +
                                        sender + " on " + victim.name + ": " +
                                        Reason(r, def, f, rawSeconds, rawMagnitude, false) + ".");
                return r;
            }

            PhotonView av = attackerView != 0 ? PhotonView.Find(attackerView) : null;
            OrbitObject attacker = av != null ? av.GetComponent<OrbitObject>() : null;
            CharacterStatuses.ApplyLocal(victim, def, seconds, magnitude, sender, attacker,
                                         Topology.ForceOf(source), true);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: character status '" + key + "' applied on request from actor " +
                                   sender + " (" + seconds + " s, magnitude " + magnitude + ", " +
                                   f.Distance.ToString("0.0") + " m).");
            return CharacterStatusRefusal.None;
        }

        // ---------------------------------------------------------------- presentation

        /// <summary>Who receives a presentation copy, judged by the owning client from sides:
        /// the viewer's, every accepted applier's, and the target's. An unknown viewer side
        /// (-1) receives only an All status.</summary>
        internal static bool InAudience(StatusVisibility rule, int viewerSide, List<int> applierSides,
                                        int targetSide)
        {
            switch (rule)
            {
                case StatusVisibility.All:
                    return true;
                case StatusVisibility.OwnForce:
                    return viewerSide >= 0 && applierSides != null && applierSides.Contains(viewerSide);
                case StatusVisibility.TargetForce:
                    return viewerSide >= 0 && viewerSide == targetSide;
                case StatusVisibility.HostileToTarget:
                    return viewerSide >= 0 && targetSide >= 0 && viewerSide != targetSide;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Announces a status on a character this client owns to its audience, after an
        /// application. A re-application whose end moved by less than
        /// <see cref="AnnounceSlackMs"/> goes only to audience members not yet told; one within
        /// <see cref="AnnounceGapSeconds"/> of the last announcement waits for the status's
        /// poll to send it.
        /// </summary>
        internal static void Announce(CharacterStatus live)
        {
            if (!live.Announces || live.Ended || live.Target == null) return;
            if (live.AnnouncedTo.Count > 0 && Time.unscaledTime < live.NextAnnounceAt)
            {
                live.AnnounceDue = true;
                return;
            }
            int remainingMs = (int)(live.RemainingSeconds * 1000f);
            if (remainingMs <= 0) return;
            int endTs = unchecked(PhotonNetwork.ServerTimestamp + remainingMs);
            bool moved = live.AnnouncedTo.Count == 0 ||
                         Math.Abs((long)unchecked(endTs - live.AnnouncedEndTs)) >= AnnounceSlackMs;

            PhotonPlayer[] peers = RuntimeAbilities.Recipients();
            int targetSide = Topology.ForceOf(live.Target);
            List<PhotonPlayer> to = new List<PhotonPlayer>(peers.Length);
            foreach (PhotonPlayer p in peers)
            {
                if (!InAudience(live.Def.Visibility, Topology.ForceOf(p), live.ApplierSides, targetSide))
                    continue;
                if (moved || !live.AnnouncedTo.Contains(p.ActorNumber)) to.Add(p);
            }
            if (to.Count == 0) return;
            EctypeNet.Send(EctypeNet.CharacterStatusApplied, to.ToArray(),
                           AppliedArgs(live.TargetActor, live.Def.Key, endTs, live.Magnitude), reliable: true);
            foreach (PhotonPlayer p in to)
                if (!live.AnnouncedTo.Contains(p.ActorNumber)) live.AnnouncedTo.Add(p.ActorNumber);
            if (moved) live.AnnouncedEndTs = endTs;
            live.NextAnnounceAt = Time.unscaledTime + AnnounceGapSeconds;
        }

        /// <summary>Tells every peer that received a copy that the status ended before the end
        /// it was told. A natural expiry sends nothing: each copy ends on the same server
        /// time.</summary>
        internal static void AnnounceEnd(CharacterStatus live, bool early)
        {
            if (!live.Announces || !early || live.AnnouncedTo.Count == 0) return;
            List<PhotonPlayer> to = new List<PhotonPlayer>(live.AnnouncedTo.Count);
            foreach (int actor in live.AnnouncedTo)
            {
                PhotonPlayer p = PlayerOf(actor);
                if (p != null) to.Add(p);
            }
            live.AnnouncedTo.Clear();
            if (to.Count == 0) return;
            EctypeNet.Send(EctypeNet.CharacterStatusApplied, to.ToArray(),
                           EndedArgs(live.TargetActor, live.Def.Key), reliable: true);
        }

        /// <summary>
        /// One received announcement (subtype <see cref="EctypeNet.CharacterStatusApplied"/>):
        /// <c>[int targetActor, string key, int endTs, float magnitude]</c> to show or extend a
        /// copy, <c>[int targetActor, string key]</c> to end it. Accepted only from the actor
        /// whose character it names.
        /// </summary>
        internal static CharacterStatusRefusal HandleApplied(int sender, object[] args)
        {
            if (!EctypeNet.MatchesLayout(EctypeNet.CharacterStatusApplied, args))
                return CharacterStatusRefusal.Failed;
            if (!AnnounceFlood.TryTake(sender.ToString(), SenderBurst, 1f / SenderBurst, Time.unscaledTime))
            {
                StatusRefusalLog.Report(sender, "character announcement flood", "*",
                                        "Ectype: refused character status announcements from actor " + sender +
                                        ": more than a burst of " + SenderBurst + ", then " + SenderBurst +
                                        " a second; they are dropped.");
                return CharacterStatusRefusal.RateLimited;
            }
            int targetActor = (int)args[0];
            string key = (string)args[1];
            if (key.Length > MaxKeyLength)
            {
                StatusRefusalLog.Report(sender, "character announcement key", "*",
                                        "Ectype: refused a character status announcement from actor " + sender +
                                        ": its key is longer than " + MaxKeyLength + " characters.");
                return CharacterStatusRefusal.Failed;
            }
            if (targetActor != sender)
            {
                StatusRefusalLog.Report(sender, "character announcement authority", "*",
                                        "Ectype: refused character status '" + key + "' announcement from actor " +
                                        sender + ": it names actor " + targetActor + "'s character, and only " +
                                        "a character's own client announces its statuses.");
                return CharacterStatusRefusal.Authority;
            }
            if (sender == EnemyStatuses.LocalActor()) return CharacterStatusRefusal.InvalidTarget;
            CharacterStatusDef def = CharacterStatuses.Lookup(key);
            if (def == null)
            {
                StatusRefusalLog.Report(sender, "character unknown key", "*",
                                        "Ectype: peer announced unknown character status '" + key +
                                        "'; ignoring (mod not installed here?).");
                return CharacterStatusRefusal.NotRegistered;
            }
            if (!def.Presented)
            {
                StatusRefusalLog.Report(sender, "character announcement", key,
                                        "Ectype: refused character status '" + key + "' announcement from actor " +
                                        sender + ": the template is not Presented on this client.");
                return CharacterStatusRefusal.NotPresented;
            }
            Player target = Topology.CharacterOf(targetActor);
            if (target == null)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: character status '" + key + "' announcement from actor " +
                                       sender + " ignored: that player has no character here.");
                return CharacterStatusRefusal.InvalidTarget;
            }
            if (args.Length == 2)
            {
                CharacterStatusCopies.Hide(target, key);
                return CharacterStatusRefusal.None;
            }
            int now = PhotonNetwork.ServerTimestamp;
            int endTs = (int)args[2];
            if (unchecked(endTs - now) <= 0)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: character status '" + key + "' announcement from actor " +
                                       sender + " arrived after its end; ignored.");
                return CharacterStatusRefusal.InvalidDuration;
            }
            if (unchecked(endTs - now) > MaxCopyMs) endTs = unchecked(now + MaxCopyMs);
            CharacterStatusCopies.Show(target, def, endTs);
            return CharacterStatusRefusal.None;
        }
    }

    /// <summary>
    /// Ends statuses and copies when a character dies. The game keeps the character object
    /// through death and respawn, so nothing is destroyed for the poll to notice. Every death
    /// passes through the life-state setter on every client: the death RPC (sent to all) sets it
    /// on each, and a client that learns of the death from a health update sets it there.
    /// </summary>
    internal static class CharacterStatusLife
    {
        internal static void ApplyPatch(Harmony harmony)
        {
            MethodInfo setter = AccessTools.PropertySetter(typeof(Player), "State");
            if (setter == null)
            {
                Plugin.Log.LogError("Ectype: Player.State setter not found - character statuses are not " +
                                    "ended on death (game update?).");
                return;
            }
            harmony.Patch(setter, postfix: new HarmonyMethod(typeof(CharacterStatusLife), "Postfix"));
        }

        private static void Postfix(Player __instance)
        {
            try
            {
                if (__instance != null && __instance.State != LifeState.Alive) OnDeath(__instance);
            }
            catch (Exception e)
            {
                if (CharacterStatuses.WarnOnce("death-hook"))
                    Plugin.Log.LogError("Ectype: ending character statuses on death failed (reported once): " + e);
            }
        }

        /// <summary>Ends every status this client runs on the character (on its owner, that
        /// also tells the peers' copies) and every copy shown on it. Returns how many ended.</summary>
        internal static int OnDeath(Player p)
        {
            int ended = CharacterStatuses.CountOn(p);
            if (ended > 0) CharacterStatuses.RemoveAll(p);
            int hidden = CharacterStatusCopies.HideAll(p);
            if (Plugin.Verbose && ended + hidden > 0)
                Plugin.Log.LogInfo("Ectype: " + p.name + " died: ended " + ended + " character status(es) and " +
                                   hidden + " presentation copy(ies).");
            return ended + hidden;
        }
    }

    /// <summary>
    /// Presentation copies of statuses on other players' characters: a visual and an
    /// IsPresented answer, on the Photon server clock. A copy holds no stat modifier and runs
    /// no callback; the status itself lives on the character's own client.
    /// </summary>
    internal static class CharacterStatusCopies
    {
        private sealed class Copy
        {
            internal Player Target;
            internal CharacterStatusDef Def;
            internal int EndTs;
            internal GameObject Vfx;
        }

        private static readonly Dictionary<string, Copy> Live = new Dictionary<string, Copy>();
        private static readonly List<string> Scratch = new List<string>();

        internal static int Count { get { return Live.Count; } }

        /// <summary>Starts a copy, or moves the end of a running one.</summary>
        internal static void Show(Player target, CharacterStatusDef def, int endTs)
        {
            string k = CharacterStatuses.MapKey(target, def.Key);
            Copy c;
            if (Live.TryGetValue(k, out c))
            {
                c.EndTs = endTs;
                return;
            }
            c = new Copy { Target = target, Def = def, EndTs = endTs };
            Live[k] = c;
            c.Vfx = CreateVfx(target, def);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: presenting character status '" + def.Key + "' on " +
                                   target.name + " for " +
                                   (unchecked(endTs - PhotonNetwork.ServerTimestamp) / 1000f).ToString("0.0") + " s.");
        }

        private static GameObject CreateVfx(Player target, CharacterStatusDef def)
        {
            if (def.CreateVfx == null) return null;
            try
            {
                GameObject go = def.CreateVfx(target);
                if (go != null) go.transform.SetParent(target.transform, false);
                return go;
            }
            catch (Exception e)
            {
                if (CharacterStatuses.WarnOnce("copyvfx:" + def.Key))
                    Plugin.Log.LogError("Ectype: character status '" + def.Key + "' CreateVfx " +
                                        "failed (suppressing further reports): " + e);
                return null;
            }
        }

        internal static void Hide(Player target, string key)
        {
            if (target == null) return;
            string k = CharacterStatuses.MapKey(target, key);
            Copy c;
            if (Live.TryGetValue(k, out c)) End(k, c);
        }

        /// <summary>Ends every copy shown on the character; returns how many.</summary>
        internal static int HideAll(Player target)
        {
            if (target == null || Live.Count == 0) return 0;
            Scratch.Clear();
            foreach (KeyValuePair<string, Copy> kv in Live)
                if (ReferenceEquals(kv.Value.Target, target)) Scratch.Add(kv.Key);
            for (int i = 0; i < Scratch.Count; i++)
            {
                Copy c;
                if (Live.TryGetValue(Scratch[i], out c)) End(Scratch[i], c);
            }
            return Scratch.Count;
        }

        internal static bool IsPresented(Player target, string key)
        {
            return target != null && Live.ContainsKey(CharacterStatuses.MapKey(target, key));
        }

        internal static float RemainingSeconds(Player target, string key)
        {
            Copy c;
            if (target == null || !Live.TryGetValue(CharacterStatuses.MapKey(target, key), out c)) return 0f;
            int ms = unchecked(c.EndTs - PhotonNetwork.ServerTimestamp);
            return ms > 0 ? ms / 1000f : 0f;
        }

        /// <summary>Ends copies whose time is up or whose character is gone.</summary>
        internal static void Poll()
        {
            if (Live.Count == 0) return;
            int now = PhotonNetwork.ServerTimestamp;
            Scratch.Clear();
            foreach (KeyValuePair<string, Copy> kv in Live)
                if (kv.Value.Target == null || unchecked(now - kv.Value.EndTs) >= 0) Scratch.Add(kv.Key);
            for (int i = 0; i < Scratch.Count; i++)
            {
                Copy c;
                if (Live.TryGetValue(Scratch[i], out c)) End(Scratch[i], c);
            }
        }

        private static void End(string k, Copy c)
        {
            Live.Remove(k);
            if (c.Vfx != null) UnityEngine.Object.Destroy(c.Vfx);
        }
    }

    /// <summary>Receivers registered with EctypeNet for the character status subtypes.</summary>
    internal static class CharacterStatusMessage
    {
        internal static void ReceiveRequest(int senderActor, object[] args)
        {
            try { CharacterStatusSync.HandleRequest(senderActor, args); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling character-status request failed: " + e);
            }
        }

        internal static void ReceiveApplied(int senderActor, object[] args)
        {
            try { CharacterStatusSync.HandleApplied(senderActor, args); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling character-status announcement failed: " + e);
            }
        }

        internal static void ReceiveResult(int senderActor, object[] args)
        {
            try { CharacterStatusSync.HandleResult(senderActor, args); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling character-status answer failed: " + e);
            }
        }
    }
}
