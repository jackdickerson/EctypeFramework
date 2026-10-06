using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Photon.Pun;
using Photon.Realtime;
using VoidManager.MPModChecks;

namespace Ectype
{
    /// <summary>What the host knows about one room member's copy of one mod.</summary>
    public enum MemberModState
    {
        /// <summary>Same version and same build hash as the host.</summary>
        Match,
        /// <summary>No mod list for this member yet. VoidManager delivers it shortly after the
        /// member arrives, or kicks them; reading the gap as "no mods" would flicker.</summary>
        NotKnownYet,
        /// <summary>Same version, but no hash to compare: the list this client holds was the
        /// hashless one (every non-host client, and a migrated host for a member whose re-sent
        /// list has not arrived).</summary>
        Unverified,
        /// <summary>The member's list does not carry the mod.</summary>
        Missing,
        /// <summary>The member runs a different version of the mod than the host.</summary>
        VersionMismatch,
        /// <summary>Same version, but the member's build hash differs from the host's.</summary>
        HashMismatch,
    }

    /// <summary>The overall outcome of checking every room member against the host.</summary>
    public enum GateVerdict
    {
        /// <summary>Every other member matches, or there is no one else (solo, offline, not in a room).</summary>
        Pass,
        /// <summary>No failure, but at least one member's list has not arrived.</summary>
        Pending,
        /// <summary>No failure and every list is in, but at least one member could only be
        /// checked by version.</summary>
        Unverified,
        /// <summary>At least one member lacks the mod or runs a different build.</summary>
        Fail,
        /// <summary>This client is in a room but is not the master: the host owns the answer.</summary>
        NotHost,
        /// <summary>The host's own list could not be read, or does not carry the mod.</summary>
        Unavailable,
    }

    /// <summary>One room member's state for the mod being checked.</summary>
    public sealed class MemberModResult
    {
        /// <summary>The room member.</summary>
        public Player Player { get; internal set; }
        /// <summary>The member's display name.</summary>
        public string Name { get; internal set; }
        /// <summary>How the member's mod list compares with the host's.</summary>
        public MemberModState State { get; internal set; }
        /// <summary>The member's version string, or null when the list is not in or lacks the mod.</summary>
        public string Version { get; internal set; }

        /// <summary>True when the member lacks the mod or runs a different version or build.</summary>
        public bool Failed
        {
            get
            {
                return State == MemberModState.Missing || State == MemberModState.VersionMismatch ||
                       State == MemberModState.HashMismatch;
            }
        }
    }

    /// <summary>The outcome of a selection gate check: a verdict plus the per-member detail behind it.</summary>
    public sealed class GateResult
    {
        /// <summary>The BepInEx plugin GUID of the mod that was checked.</summary>
        public string ModGuid { get; internal set; }
        /// <summary>The overall outcome.</summary>
        public GateVerdict Verdict { get; internal set; }
        /// <summary>The host's version of the mod, when readable.</summary>
        public string HostVersion { get; internal set; }
        /// <summary>Why the verdict is Unavailable; null otherwise.</summary>
        public string Reason { get; internal set; }
        /// <summary>Every other room member, in Photon's player-list order. Empty off-host.</summary>
        public IList<MemberModResult> Members { get; internal set; }

        /// <summary>True when the verdict is <see cref="GateVerdict.Fail"/>.</summary>
        public bool Failed { get { return Verdict == GateVerdict.Fail; } }

        /// <summary>The names of the members in a failed state.</summary>
        public IList<string> FailingNames
        {
            get { return Members.Where(m => m.Failed).Select(m => m.Name).ToList(); }
        }

        /// <summary>"Alice (missing), Bob (version 0.3.1)" for every member in a failed state.</summary>
        public string DescribeFailures()
        {
            return string.Join(", ", Members.Where(m => m.Failed).Select(Describe).ToArray());
        }

        /// <summary>Every member not in Match, described the same way.</summary>
        public string DescribeNonMatching()
        {
            return string.Join(", ", Members.Where(m => m.State != MemberModState.Match)
                                            .Select(Describe).ToArray());
        }

        private static string Describe(MemberModResult m)
        {
            switch (m.State)
            {
                case MemberModState.Missing: return m.Name + " (missing)";
                case MemberModState.VersionMismatch: return m.Name + " (version " + m.Version + ")";
                case MemberModState.HashMismatch: return m.Name + " (different build of " + m.Version + ")";
                case MemberModState.NotKnownYet: return m.Name + " (mod list not received)";
                case MemberModState.Unverified: return m.Name + " (version only, no hash)";
                default: return m.Name;
            }
        }

        internal string Signature()
        {
            return Verdict + "|" + HostVersion + "|" + string.Join(";", Members.Select(
                m => (m.Player != null ? m.Player.ActorNumber : -1) + ":" + m.State).ToArray());
        }
    }

    /// <summary>
    /// Host-side answer to "does every room member run mod X at the host's build?".
    ///
    /// Both join gates (VoidManager's mod-list check and the game's required-asset check) run
    /// only when a player joins, so a member already in the room when a mixed mod's content is
    /// selected is never re-checked. A mod registers its GUID here and applies its own policy
    /// (refuse, revert, warn) from the result.
    ///
    /// The host's side is VoidManager's own mod list, read by reflection, never rebuilt from the
    /// chainloader: both sides of a hash comparison must come from the one producer. Each peer's
    /// side is VoidManager's networked-peer record, which carries hashes on the host only.
    /// Room-lifecycle events are bound once here, not per mod.
    ///
    /// The presence queries at the bottom are hashless and work on any client.
    /// </summary>
    public static class SelectionGate
    {
        /// <summary>Raised on the main thread whenever a registered mod's result changes.</summary>
        public static event Action<GateResult> Changed;

        private static readonly Dictionary<string, List<Action<GateResult>>> Registered =
            new Dictionary<string, List<Action<GateResult>>>();
        private static readonly Dictionary<string, GateResult> Last = new Dictionary<string, GateResult>();
        private static readonly Dictionary<string, string> LastSig = new Dictionary<string, string>();

        private static bool _bound;
        private static FieldInfo _myModListField;
        private static bool _loggedHostListMiss;
        private static MethodInfo _sendModlistToHost;

        /// <summary>
        /// Start tracking a mod. <paramref name="onChanged"/> (optional) is that mod's failure
        /// policy: it runs whenever the result for this GUID changes, including once now.
        /// Safe to call more than once; every callback is kept.
        /// </summary>
        public static void Register(string modGuid, Action<GateResult> onChanged = null)
        {
            if (string.IsNullOrEmpty(modGuid)) throw new ArgumentException("modGuid is empty", "modGuid");
            EnsureBound();
            List<Action<GateResult>> list;
            if (!Registered.TryGetValue(modGuid, out list))
                Registered[modGuid] = list = new List<Action<GateResult>>();
            if (onChanged != null) list.Add(onChanged);
            Reevaluate(null, "registered " + modGuid);
        }

        /// <summary>The last published result for a registered mod, or a fresh evaluation.</summary>
        public static GateResult Current(string modGuid)
        {
            GateResult r;
            return Last.TryGetValue(modGuid, out r) ? r : Evaluate(modGuid);
        }

        /// <summary>Evaluate now, without publishing. Works for any GUID, registered or not.</summary>
        public static GateResult Evaluate(string modGuid)
        {
            return Evaluate(modGuid, null);
        }

        // ------------------------------------------------------------------ evaluation

        private static GateResult Evaluate(string modGuid, Player leaving)
        {
            GateResult r = new GateResult { ModGuid = modGuid, Members = new List<MemberModResult>() };
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode)
                {
                    r.Verdict = GateVerdict.Pass;
                    return r;
                }
                if (!PhotonNetwork.IsMasterClient)
                {
                    r.Verdict = GateVerdict.NotHost;
                    return r;
                }

                GateResult unavailable;
                MPModDataBlock mine = HostBlock(modGuid, out unavailable);
                if (mine == null) return unavailable;
                r.HostVersion = mine.Version;
                bool hostHashKnown = HashKnown(mine.Hash);

                NetworkedPeerManager peers = NetworkedPeerManager.Instance;
                Player[] others = PhotonNetwork.PlayerListOthers ?? new Player[0];
                bool pending = false, unverified = false, failed = false;
                foreach (Player p in others)
                {
                    if (p == null || p.IsInactive) continue;
                    // VoidManager raises PlayerLeftRoom before dropping the leaver's record, and
                    // Photon may still list them, so the leaver is excluded by identity.
                    if (leaving != null && p.ActorNumber == leaving.ActorNumber) continue;

                    MemberModResult m = new MemberModResult { Player = p, Name = NameOf(p) };
                    r.Members.Add(m);

                    // Existence first: GetNetworkedPeerMods decodes and caches the hashless
                    // property on a miss, so it is only called once a record is known to exist.
                    if (peers == null || !peers.GetNetworkedPeerModlistExists(p))
                    {
                        m.State = MemberModState.NotKnownYet;
                        pending = true;
                        continue;
                    }
                    MPUserDataBlock list = peers.GetNetworkedPeerMods(p);
                    MPModDataBlock theirs = list != null && list.ModData != null
                        ? list.ModData.FirstOrDefault(b => b != null && b.ModGUID == modGuid)
                        : null;

                    if (theirs == null) { m.State = MemberModState.Missing; failed = true; continue; }
                    m.Version = theirs.Version;
                    if (theirs.Version != mine.Version)
                    {
                        m.State = MemberModState.VersionMismatch; failed = true; continue;
                    }
                    // A hashless record carries 32 zero bytes, not null; both mean "never sent".
                    if (!hostHashKnown || !HashKnown(theirs.Hash))
                    {
                        m.State = MemberModState.Unverified; unverified = true; continue;
                    }
                    if (!theirs.Hash.SequenceEqual(mine.Hash))
                    {
                        m.State = MemberModState.HashMismatch; failed = true; continue;
                    }
                    m.State = MemberModState.Match;
                }

                r.Verdict = failed ? GateVerdict.Fail
                          : pending ? GateVerdict.Pending
                          : unverified ? GateVerdict.Unverified
                          : GateVerdict.Pass;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: selection gate evaluation for " + modGuid + " threw: " + e);
                r.Verdict = GateVerdict.Unavailable;
                r.Reason = "evaluation threw: " + e.GetType().Name;
            }
            return r;
        }

        private static MPModDataBlock HostBlock(string modGuid, out GateResult failure)
        {
            failure = new GateResult { ModGuid = modGuid, Members = new List<MemberModResult>(),
                                       Verdict = GateVerdict.Unavailable };
            MPModDataBlock[] mine = OwnModList();
            if (mine == null)
            {
                failure.Reason = "VoidManager's own mod list is not readable";
                return null;
            }
            MPModDataBlock block = mine.FirstOrDefault(b => b != null && b.ModGUID == modGuid);
            if (block == null) failure.Reason = "the host's mod list does not carry " + modGuid;
            return block;
        }

        /// <summary>This client's list as VoidManager built it: MPModCheckManager.MyModList
        /// (private field). The host's side of every comparison, and what a resend carries.</summary>
        private static MPModDataBlock[] OwnModList()
        {
            MPModCheckManager mgr = MPModCheckManager.Instance;
            if (mgr == null) return null;
            if (_myModListField == null)
                _myModListField = typeof(MPModCheckManager).GetField("MyModList",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_myModListField == null)
            {
                if (!_loggedHostListMiss)
                {
                    _loggedHostListMiss = true;
                    Plugin.Log.LogError("Ectype: MPModCheckManager.MyModList not found (VoidManager " +
                                        "update?) - every selection gate reports Unavailable.");
                }
                return null;
            }
            return _myModListField.GetValue(mgr) as MPModDataBlock[];
        }

        private static bool HashKnown(byte[] hash)
        {
            if (hash == null || hash.Length == 0) return false;
            for (int i = 0; i < hash.Length; i++) if (hash[i] != 0) return true;
            return false;
        }

        private static string NameOf(Player p)
        {
            string n = p.NickName;
            return string.IsNullOrEmpty(n) ? "player " + p.ActorNumber : n;
        }

        // ------------------------------------------------------------------ events

        /// <summary>Bind VoidManager's room-lifecycle events once. Called from the framework's
        /// Awake and again from Register, so a consumer loaded first still gets a binding.</summary>
        internal static void EnsureBound()
        {
            if (_bound) return;
            VoidManager.Events ev = VoidManager.Events.Instance;
            if (ev == null) return;
            _bound = true;
            ev.ClientModlistRecieved += (s, a) => Reevaluate(null, "mod list received");
            ev.PlayerLeftRoom += (s, a) => Reevaluate(a != null ? a.player : null, "player left");
            ev.MasterClientSwitched += (s, a) =>
            {
                ResendModListToNewHost();
                Reevaluate(null, "host switched");
            };
            // A new member is "not known yet" until their list arrives.
            ev.PlayerEnteredRoom += (s, a) => Reevaluate(null, "player entered");
            ev.JoinedRoom += (s, a) => Reevaluate(null, "joined room");
            ev.LeftRoom += (s, a) => Reevaluate(null, "left room");
        }

        /// <summary>
        /// VoidManager sends a client's hashfull list to the master once, at join, so a host that
        /// inherits the room holds only hashless records for the members already in it. Each
        /// non-master client re-sends its list through VoidManager's own send when the master
        /// changes. The master-side handler only stores the list and raises
        /// ClientModlistRecieved; the host's mod check and kick run from a join-time coroutine,
        /// not from receipt, so a repeat cannot kick. Photon updates the room's master id before
        /// raising the switch callback, so the send already reaches the new master.
        /// </summary>
        private static void ResendModListToNewHost()
        {
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode || PhotonNetwork.IsMasterClient) return;
                MPModDataBlock[] mine = OwnModList();
                if (_sendModlistToHost == null)
                    _sendModlistToHost = typeof(NetworkedPeerManager).GetMethod("SendModlistToHost",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (mine == null || _sendModlistToHost == null)
                {
                    Plugin.Log.LogError("Ectype: cannot re-send the mod list to the new host (" +
                                        (mine == null ? "MPModCheckManager.MyModList" :
                                         "NetworkedPeerManager.SendModlistToHost") +
                                        " not found; VoidManager update?) - it will see this client as Unverified.");
                    return;
                }
                _sendModlistToHost.Invoke(null, new object[] { mine });
                if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: re-sent hashfull mod list to the new host.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: re-sending the mod list to the new host threw: " + e);
            }
        }

        /// <summary>Re-evaluate every registered mod and publish the ones whose result changed.</summary>
        public static void Reevaluate()
        {
            Reevaluate(null, "requested");
        }

        private static void Reevaluate(Player leaving, string why)
        {
            try
            {
                foreach (KeyValuePair<string, List<Action<GateResult>>> kv in Registered.ToList())
                {
                    GateResult r = Evaluate(kv.Key, leaving);
                    string sig = r.Signature();
                    string prev;
                    if (LastSig.TryGetValue(kv.Key, out prev) && prev == sig) continue;
                    LastSig[kv.Key] = sig;
                    Last[kv.Key] = r;

                    if (r.Verdict == GateVerdict.Fail)
                        Plugin.Log.LogWarning("Ectype: selection gate for " + kv.Key + " FAILS (" + why +
                                              "): " + r.DescribeFailures());
                    else if (r.Verdict == GateVerdict.Unavailable)
                        Plugin.Log.LogWarning("Ectype: selection gate for " + kv.Key +
                                              " unavailable (" + why + "): " + r.Reason);
                    else if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: selection gate for " + kv.Key + " is " + r.Verdict +
                                           " (" + why + ")" + (r.Members.Count > 0
                                               ? ": " + r.DescribeNonMatching() : ""));

                    foreach (Action<GateResult> cb in kv.Value) Publish(cb, r);
                    Action<GateResult> any = Changed;
                    if (any != null)
                        foreach (Action<GateResult> cb in any.GetInvocationList()) Publish(cb, r);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: selection gate re-evaluation (" + why + ") threw: " + e);
            }
        }

        private static void Publish(Action<GateResult> cb, GateResult r)
        {
            try { cb(r); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: a selection gate callback for " + r.ModGuid + " threw: " + e);
            }
        }

        // ------------------------------------------------------------------ presence (any client)

        /// <summary>
        /// Players VoidManager holds a mod list for that carries the mod. Hashless, so it answers on
        /// every client; the local player is included when VoidManager recorded them. Empty before
        /// VoidManager is up.
        /// </summary>
        public static List<Player> PeersWithMod(string modGuid)
        {
            NetworkedPeerManager peers = NetworkedPeerManager.Instance;
            if (peers == null) return new List<Player>();
            return peers.NetworkedPeersWithMod(modGuid) ?? new List<Player>();
        }

        /// <summary>Whether a player's mod list, as VoidManager holds it, carries the mod. Hashless.</summary>
        public static bool PeerHasMod(Player player, string modGuid)
        {
            NetworkedPeerManager peers = NetworkedPeerManager.Instance;
            return peers != null && player != null && peers.NetworkedPeerHasMod(player, modGuid);
        }
    }
}
