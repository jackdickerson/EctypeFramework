using System;
using System.Collections.Generic;

using CG.Game.Player;
using CG.Ship.Object;
using CG.Space;
using Photon.Pun;
using UnityEngine;

using PhotonPlayer = Photon.Realtime.Player;

namespace Ectype
{
    /// <summary>Why <see cref="CharacterStatuses.Request"/> or
    /// <see cref="CharacterStatuses.TryRemove"/> did nothing, and why a character's client
    /// refused a received request or announcement.</summary>
    public enum CharacterStatusRefusal
    {
        /// <summary>Not refused: applied, sent or removed.</summary>
        None = 0,
        /// <summary>The target is null, has no stat collection or no PhotonView, or a received
        /// request names a character this client does not own.</summary>
        InvalidTarget = 1,
        /// <summary>No template is registered under the key on this client.</summary>
        NotRegistered = 2,
        /// <summary>The framework's patches did not apply this session.</summary>
        Inert = 3,
        /// <summary>The duration is zero, negative or not finite.</summary>
        InvalidDuration = 4,
        /// <summary>The magnitude is negative or not finite.</summary>
        InvalidMagnitude = 5,
        /// <summary>A request for a template that does not set Replicated.</summary>
        NotReplicated = 6,
        /// <summary>The target's client does not run this framework on this network contract,
        /// so it has nothing to receive a request with.</summary>
        NoPeer = 7,
        /// <summary>The target character is dead.</summary>
        TargetDead = 8,
        /// <summary>The requester has no character on the judging client.</summary>
        SenderMissing = 9,
        /// <summary>The requester's character is dead.</summary>
        SenderDead = 10,
        /// <summary>The requester's relation to the target fails the template's Stance.</summary>
        Stance = 11,
        /// <summary>The requester's character is farther from the target than MaxRange.</summary>
        OutOfRange = 12,
        /// <summary>RequiresLineOfSight is set and level geometry blocks the line between the
        /// two characters.</summary>
        NoLineOfSight = 13,
        /// <summary>The requester sent this key faster than MinInterval allows beyond Burst.</summary>
        RateLimited = 14,
        /// <summary>A removal of, or an announcement for, a status on a character the sender
        /// does not own.</summary>
        Authority = 15,
        /// <summary>An announcement for a template that does not set Presented on this client.</summary>
        NotPresented = 16,
        /// <summary>The call threw, or a received message was malformed.</summary>
        Failed = 17,
    }

    /// <summary>What the judging client sees of a request: the target's and the requester's
    /// characters, as this client simulates them. Gathered from the game, or built by hand in
    /// the dev self-test.</summary>
    internal struct CharacterRequestFacts
    {
        internal bool VictimAlive;
        internal bool SourceKnown;
        internal bool SourceAlive;
        internal bool Hostile;
        internal float Distance;
        internal bool LineClear;
    }

    /// <summary>
    /// A token bucket per id: Burst tokens to start with, one more every MinInterval seconds
    /// up to Burst, one taken per accepted request. A refused request takes nothing.
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
        /// Always true when the template sets no MinInterval.</summary>
        internal bool TryTake(string id, CharacterStatusDef def, float now)
        {
            if (!(def.MinInterval > 0f)) return true;
            float burst = Math.Max(1, def.Burst);
            Bucket b;
            float tokens = burst;
            if (_buckets.TryGetValue(id, out b))
                tokens = Math.Min(burst, b.Tokens + Math.Max(0f, now - b.At) / def.MinInterval);
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
    /// Request: the requester sends (target actor, key, duration, magnitude, attacker view)
    /// to the target's client alone. That client judges the request against its own view of
    /// the requester's character - never against anything the message claims about position
    /// or state - in a fixed order: template registered and Replicated; duration and
    /// magnitude in bounds (cut to MaxSeconds and MaxMagnitude); target alive; requester has
    /// a character and it is alive; Stance by the game's different-faction check; MaxRange;
    /// line of sight; rate. It then applies the status exactly as a local Apply would. The
    /// requester runs the same checks first, from its own view, and sends nothing a check
    /// refuses.
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

        /// <summary>Accepted requests on the target's client, per requester and key.</summary>
        internal static readonly CharacterStatusRate Accepted = new CharacterStatusRate();

        /// <summary>Sent requests on the requester's client, per target and key: the same
        /// budget, so a requester that keeps to it is never refused for rate.</summary>
        internal static readonly CharacterStatusRate Sent = new CharacterStatusRate();

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
            if (!Finite(def.MaxMagnitude) || def.MaxMagnitude < 0f)
                return "MaxMagnitude is " + def.MaxMagnitude + "; it must be a finite number, 0 or more";
            return null;
        }

        /// <summary>One warning per field group a template sets without the switch that uses it.</summary>
        internal static void WarnUnusedFields(CharacterStatusDef def)
        {
            if (!def.Replicated && (def.Stance != StatusStance.Any || def.MaxRange > 0f ||
                                    def.RequiresLineOfSight || def.MinInterval > 0f))
                Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' sets request " +
                                      "checks (Stance, MaxRange, RequiresLineOfSight or MinInterval) " +
                                      "but is not Replicated, so it accepts no requests for them to judge.");
            if (!def.Presented && (def.Visibility != StatusVisibility.All || def.CreateVfx != null))
                Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' sets Visibility " +
                                      "or CreateVfx but is not Presented, so no presentation copy is sent.");
        }

        /// <summary>Apply on another player's character: it lands on this client's copy,
        /// which none of that player's damage or movement reads.</summary>
        internal static void WarnNotOwned(CharacterStatusDef def, Player target)
        {
            if (!CharacterStatuses.WarnOnce("notmine:" + def.Key)) return;
            Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' applied to " +
                                  target.name + ", a character this client does not own. It " +
                                  "runs on this client's copy only and changes nothing for " +
                                  "that player; " + (def.Replicated
                                      ? "use CharacterStatuses.Request to ask that player's client."
                                      : "mark the template Replicated and use CharacterStatuses.Request " +
                                        "to ask that player's client."));
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
            if (!(seconds > 0f) || float.IsInfinity(seconds)) return CharacterStatusRefusal.InvalidDuration;
            if (!Finite(magnitude) || magnitude < 0f) return CharacterStatusRefusal.InvalidMagnitude;
            if (seconds > def.MaxSeconds) seconds = def.MaxSeconds;
            if (magnitude > def.MaxMagnitude) magnitude = def.MaxMagnitude;
            return CharacterStatusRefusal.None;
        }

        /// <summary>
        /// Every check but rate, in order. Pure: the facts are whatever the caller saw, so the
        /// requester and the target's client run the same function on their own views.
        /// </summary>
        internal static CharacterStatusRefusal Judge(CharacterStatusDef def, ref float seconds,
                                                     ref float magnitude, CharacterRequestFacts f)
        {
            if (def == null) return CharacterStatusRefusal.NotRegistered;
            if (!def.Replicated) return CharacterStatusRefusal.NotReplicated;
            CharacterStatusRefusal bounds = Bound(def, ref seconds, ref magnitude);
            if (bounds != CharacterStatusRefusal.None) return bounds;
            if (!f.VictimAlive) return CharacterStatusRefusal.TargetDead;
            if (!f.SourceKnown) return CharacterStatusRefusal.SenderMissing;
            if (!f.SourceAlive) return CharacterStatusRefusal.SenderDead;
            if (def.Stance == StatusStance.Hostile && !f.Hostile) return CharacterStatusRefusal.Stance;
            if (def.Stance == StatusStance.Friendly && f.Hostile) return CharacterStatusRefusal.Stance;
            if (def.MaxRange > 0f && !(f.Distance <= def.MaxRange)) return CharacterStatusRefusal.OutOfRange;
            if (def.RequiresLineOfSight && !f.LineClear) return CharacterStatusRefusal.NoLineOfSight;
            return CharacterStatusRefusal.None;
        }

        /// <summary>What this client sees of the two characters. The line of sight is cast
        /// only when the template asks for it.</summary>
        internal static CharacterRequestFacts Gather(Player source, Player victim, CharacterStatusDef def)
        {
            CharacterRequestFacts f = new CharacterRequestFacts();
            f.VictimAlive = Alive(victim);
            f.SourceKnown = source != null;
            if (source == null || victim == null) return f;
            f.SourceAlive = Alive(source);
            f.Hostile = Topology.Hostile(source, victim);
            f.Distance = Vector3.Distance(source.transform.position, victim.transform.position);
            f.LineClear = def == null || !def.RequiresLineOfSight || LineClear(source, victim);
            return f;
        }

        /// <summary>The game's replicated life state: NetworkNotifyDeath sets it to Dead on
        /// every client and the respawn sets it back, where the health component's own flag is
        /// set only on the owning client.</summary>
        internal static bool Alive(Player p)
        {
            return p != null && p.State == LifeState.Alive;
        }

        /// <summary>Chest to chest against level geometry, triggers ignored.</summary>
        internal static bool LineClear(Player source, Player victim)
        {
            int mask = SightMask();
            if (mask == 0) return false;
            Vector3 a = source.transform.position + source.transform.up * ChestHeight;
            Vector3 b = victim.transform.position + victim.transform.up * ChestHeight;
            return !Physics.Linecast(a, b, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// The layers that stop a line of sight: StaticPlatform and MovingPlatform, the hull and
        /// level geometry. They are the interaction ray's mask without InteractiveObjects, and
        /// the pair the jetpack treats as environment. Interactive objects are left out so a
        /// console or a carried item does not hide a player standing behind it. Characters sit
        /// on their own layer, so neither end of the line hits its own body. Resolved by name
        /// once; a game without the layers fails every line-of-sight check closed.
        /// </summary>
        internal static int SightMask()
        {
            if (_sightMask >= 0) return _sightMask;
            _sightMask = LayerMask.GetMask("StaticPlatform", "MovingPlatform");
            if (_sightMask == 0)
                Plugin.Log.LogError("Ectype: the game has no 'StaticPlatform' or 'MovingPlatform' " +
                                    "layer; every character status that requires line of sight is " +
                                    "refused until the framework is updated for this game version.");
            return _sightMask;
        }

        /// <summary>A refusal in words, the same on the requester and on the target's client.
        /// <paramref name="seconds"/> and <paramref name="magnitude"/> are the values as received.</summary>
        internal static string Reason(CharacterStatusRefusal r, CharacterStatusDef def, CharacterRequestFacts f,
                                      float seconds, float magnitude)
        {
            switch (r)
            {
                case CharacterStatusRefusal.NotReplicated:
                    return "the template is not Replicated, so it accepts no requests";
                case CharacterStatusRefusal.InvalidDuration:
                    return "duration " + seconds + " s is not a positive finite number";
                case CharacterStatusRefusal.InvalidMagnitude:
                    return "magnitude " + magnitude + " is negative or not a finite number";
                case CharacterStatusRefusal.TargetDead:
                    return "the target character is dead";
                case CharacterStatusRefusal.SenderMissing:
                    return "the requester has no character on this client";
                case CharacterStatusRefusal.SenderDead:
                    return "the requester's character is dead";
                case CharacterStatusRefusal.Stance:
                    return "stance " + def.Stance + " and the requester is " +
                           (f.Hostile ? "on another side from the target" : "on the target's side");
                case CharacterStatusRefusal.OutOfRange:
                    return "the requester is " + f.Distance.ToString("0.0") + " m from the target, " +
                           "beyond MaxRange " + def.MaxRange;
                case CharacterStatusRefusal.NoLineOfSight:
                    return "level geometry blocks the line between the requester and the target";
                case CharacterStatusRefusal.RateLimited:
                    return "more than " + def.Burst + " request(s) at once, or more often than one per " +
                           def.MinInterval + " s";
                case CharacterStatusRefusal.NoPeer:
                    return "the target's client does not run this framework on network contract " +
                           EctypePluginInfo.NET_CONTRACT_VERSION;
                default:
                    return r.ToString();
            }
        }

        // ---------------------------------------------------------------- requesting

        /// <summary>The request half of <see cref="CharacterStatuses.Request"/>, after the
        /// template lookup.</summary>
        internal static CharacterStatusRefusal Request(Player target, CharacterStatusDef def, float seconds,
                                                       float magnitude, OrbitObject attacker)
        {
            if (target.photonView == null) return CharacterStatusRefusal.InvalidTarget;
            if (target.photonView.IsMine)
            {
                if (!(seconds > 0f) || float.IsInfinity(seconds))
                {
                    Plugin.Log.LogWarning("Ectype: Request('" + def.Key + "') with duration " +
                                          seconds + "s ignored.");
                    return CharacterStatusRefusal.InvalidDuration;
                }
                CharacterStatuses.ApplyLocal(target, def, seconds, magnitude, 0, attacker, LocalSide());
                return CharacterStatusRefusal.None;
            }

            int targetActor = Topology.ActorOf(target);
            PhotonPlayer owner = PlayerOf(targetActor);
            Player me = Topology.CharacterOf(EnemyStatuses.LocalActor());
            CharacterRequestFacts f = Gather(me, target, def);
            float s = seconds, m = magnitude;
            CharacterStatusRefusal r = Judge(def, ref s, ref m, f);
            if (r == CharacterStatusRefusal.None && (owner == null || !PeerPresence.HasFramework(owner)))
                r = CharacterStatusRefusal.NoPeer;
            if (r == CharacterStatusRefusal.None &&
                !Sent.TryTake(targetActor + "/" + def.Key, def, Time.unscaledTime))
                r = CharacterStatusRefusal.RateLimited;
            if (r != CharacterStatusRefusal.None)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: Request('" + def.Key + "') on " + target.name +
                                       " refused: " + Reason(r, def, f, seconds, magnitude) + ".");
                return r;
            }

            int attackerView = attacker != null && attacker.photonView != null ? attacker.photonView.ViewID : 0;
            EctypeNet.Send(EctypeNet.CharacterStatusRequest, new[] { owner },
                           RequestArgs(targetActor, def.Key, s, m, attackerView), reliable: true);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: character status '" + def.Key + "' requested on " +
                                   target.name + " (actor " + targetActor + ", " + s + " s, magnitude " +
                                   m + ").");
            return CharacterStatusRefusal.None;
        }

        /// <summary>[int targetActor, string key, int durationMs, float magnitude, int attackerView].</summary>
        internal static object[] RequestArgs(int targetActor, string key, float seconds, float magnitude,
                                             int attackerView)
        {
            int ms = (int)Math.Min(Math.Round(seconds * 1000.0), int.MaxValue);
            return new object[] { targetActor, key, Math.Max(ms, 1), magnitude, attackerView };
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

        // ---------------------------------------------------------------- receiving a request

        /// <summary>
        /// One received request (subtype <see cref="EctypeNet.CharacterStatusRequest"/>).
        /// <paramref name="sender"/> is the actor Photon stamped on the event, the one fact
        /// about the requester the message cannot forge. Returns what was refused, for the
        /// dev self-test; a refusal is logged here.
        /// </summary>
        internal static CharacterStatusRefusal HandleRequest(int sender, object[] args)
        {
            if (!EctypeNet.MatchesLayout(EctypeNet.CharacterStatusRequest, args))
                return CharacterStatusRefusal.Failed;
            int targetActor = (int)args[0];
            string key = (string)args[1];
            int durationMs = (int)args[2];
            float magnitude = (float)args[3];
            int attackerView = (int)args[4];

            int me = EnemyStatuses.LocalActor();
            Player victim = targetActor == me ? Topology.CharacterOf(me) : null;
            if (victim == null || victim.photonView == null || !victim.photonView.IsMine)
            {
                StatusRefusalLog.Report(sender, "character request", key,
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
                if (CharacterStatuses.WarnOnce("peer-nodef:" + key))
                    Plugin.Log.LogWarning("Ectype: peer requested unknown character status '" + key +
                                          "'; ignoring (mod not installed here?).");
                return CharacterStatusRefusal.NotRegistered;
            }

            Player source = Topology.CharacterOf(sender);
            CharacterRequestFacts f = Gather(source, victim, def);
            float seconds = durationMs / 1000f, rawSeconds = seconds, rawMagnitude = magnitude;
            CharacterStatusRefusal r = Judge(def, ref seconds, ref magnitude, f);
            if (r == CharacterStatusRefusal.None &&
                !Accepted.TryTake(sender + "/" + key, def, Time.unscaledTime))
                r = CharacterStatusRefusal.RateLimited;
            if (r != CharacterStatusRefusal.None)
            {
                StatusRefusalLog.Report(sender, "character request", key,
                                        "Ectype: refused character status '" + key + "' request from actor " +
                                        sender + " on " + victim.name + ": " +
                                        Reason(r, def, f, rawSeconds, rawMagnitude) + ".");
                return r;
            }

            PhotonView av = attackerView != 0 ? PhotonView.Find(attackerView) : null;
            OrbitObject attacker = av != null ? av.GetComponent<OrbitObject>() : null;
            CharacterStatuses.ApplyLocal(victim, def, seconds, magnitude, sender, attacker,
                                         Topology.ForceOf(source));
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
        /// <see cref="AnnounceSlackMs"/> goes only to audience members not yet told.
        /// </summary>
        internal static void Announce(CharacterStatus live)
        {
            if (!live.Announces || live.Ended || live.Target == null) return;
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
            int targetActor = (int)args[0];
            string key = (string)args[1];
            if (targetActor != sender)
            {
                StatusRefusalLog.Report(sender, "character announcement", key,
                                        "Ectype: refused character status '" + key + "' announcement from actor " +
                                        sender + ": it names actor " + targetActor + "'s character, and only " +
                                        "a character's own client announces its statuses.");
                return CharacterStatusRefusal.Authority;
            }
            if (sender == EnemyStatuses.LocalActor()) return CharacterStatusRefusal.InvalidTarget;
            CharacterStatusDef def = CharacterStatuses.Lookup(key);
            if (def == null)
            {
                if (CharacterStatuses.WarnOnce("peer-nodef:" + key))
                    Plugin.Log.LogWarning("Ectype: peer announced unknown character status '" + key +
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
            int endTs = (int)args[2];
            if (unchecked(endTs - PhotonNetwork.ServerTimestamp) <= 0)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: character status '" + key + "' announcement from actor " +
                                       sender + " arrived after its end; ignored.");
                return CharacterStatusRefusal.InvalidDuration;
            }
            CharacterStatusCopies.Show(target, def, endTs);
            return CharacterStatusRefusal.None;
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

    /// <summary>Receivers registered with EctypeNet for the two character status subtypes.</summary>
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
    }
}
