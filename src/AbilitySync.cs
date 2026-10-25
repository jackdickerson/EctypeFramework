using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using HarmonyLib;
using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Character.Abilities;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace Ectype
{
    /// <summary>
    /// Replicates the active state of runtime-installed Opsive abilities by KEY, on a
    /// channel the framework owns, instead of by array index on the game's own RPC: the
    /// owner's vanilla send is suppressed and (view id, key, active) goes out on the
    /// framework's own Photon event (EctypeNet) to the peers that run this framework, each of
    /// which resolves the key against its own replica's array and ignores keys it has no
    /// ability for. Array order across clients stops mattering.
    ///
    /// Two registration rules, both enforced by the sender rather than the receiver:
    ///  * the key must be computed from the same literal on every client - a string the
    ///    installing mod owns, namespaced by that mod, never derived from runtime state;
    ///  * an unregistered runtime ability still goes through the vanilla RPC and still
    ///    breaks the index premise. Registering is what opts it in.
    /// </summary>
    public static class RuntimeAbilities
    {
        // Weak so a key dies with its ability rather than pinning every dead character's
        // abilities for the life of the process.
        private static readonly ConditionalWeakTable<Ability, string> Keys =
            new ConditionalWeakTable<Ability, string>();

        /// <summary>
        /// Opt an installed ability into key-based replication. Call after the ability is
        /// in its locomotion's array; the key must be identical on every client for the
        /// same ability (a literal, namespaced by the registering mod).
        /// </summary>
        public static void Register(Ability ability, string key)
        {
            if (ability == null) throw new ArgumentNullException("ability");
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is empty", "key");
            Keys.Remove(ability);
            Keys.Add(ability, key);
        }

        /// <summary>Forgets the key registered for an ability; a no-op for null or an unregistered one.</summary>
        public static void Unregister(Ability ability)
        {
            if (ability != null) Keys.Remove(ability);
        }

        /// <summary>The registered key, or null for a prefab (or unregistered) ability.</summary>
        public static string KeyOf(Ability ability)
        {
            string key;
            return ability != null && Keys.TryGetValue(ability, out key) ? key : null;
        }

        internal static Ability Find(UltimateCharacterLocomotion loco, string key)
        {
            Ability[] abilities = loco != null ? loco.Abilities : null;
            if (abilities == null) return null;
            for (int i = 0; i < abilities.Length; i++)
            {
                string k;
                if (abilities[i] != null && Keys.TryGetValue(abilities[i], out k) && k == key)
                    return abilities[i];
            }
            return null;
        }

        // ------------------------------------------------------------------ send

        /// <summary>Peers that run this framework, excluding this client. Empty in solo.</summary>
        internal static Player[] Recipients()
        {
            try
            {
                List<Player> with = PeerPresence.PeersWithFramework();
                if (with.Count == 0) return new Player[0];
                Player me = PhotonNetwork.LocalPlayer;
                List<Player> others = new List<Player>(with.Count);
                foreach (Player p in with)
                    if (p != null && (me == null || p.ActorNumber != me.ActorNumber)) others.Add(p);
                return others.ToArray();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: resolving ability-sync recipients failed: " + e);
                return new Player[0];
            }
        }

        internal static void Send(Player[] to, int viewId, string key, bool active,
                                  object[] startData, object[] extendedStartData, bool lateJoin)
        {
            if (to == null || to.Length == 0) return;
            EctypeNet.Send(EctypeNet.AbilityActive, to,
                           new object[]
                           {
                               viewId, key, active,
                               startData ?? new object[0],
                               extendedStartData ?? new object[0],
                               lateJoin
                           },
                           reliable: true);
        }

        internal static object[] ExtendedStartData(Ability ability)
        {
            IExtendedStartSyncAbility ext = ability as IExtendedStartSyncAbility;
            return ext != null ? ext.StartData : null;
        }
    }

    /// <summary>
    /// Receiver, registered with EctypeNet as subtype <see cref="EctypeNet.AbilityActive"/>.
    /// Arguments: [int viewId, string key, bool active, object[] startData,
    ///             object[] extendedStartData, bool lateJoin].
    /// </summary>
    internal static class AbilityActiveMessage
    {
        private static readonly HashSet<string> ReportedOwnerMismatch = new HashSet<string>();

        // A start that arrived before its character could run it, keyed "viewId/key". The
        // driver retries it; any later message for the same view and key replaces or cancels it.
        private sealed class HeldStart
        {
            internal int ViewId;
            internal string Key;
            internal object[] StartData;
            internal object[] Extended;
            internal bool LateJoin;
            internal float Deadline;
        }

        private const float HoldSeconds = 5f;
        private const float RetryInterval = 0.1f;
        private static readonly Dictionary<string, HeldStart> Held =
            new Dictionary<string, HeldStart>();
        private static float _nextRetry;

        /// <summary>EctypeNet entry point: resolves the sending actor to its player in the
        /// current room (null when it is not there) and handles the message.</summary>
        internal static void Receive(int senderActor, object[] args)
        {
            Room room = PhotonNetwork.CurrentRoom;
            Handle(args, room != null ? room.GetPlayer(senderActor) : null);
        }

        private static void Handle(object[] args, Player sender)
        {
            try
            {
                if (args == null || args.Length < 6) return;
                int viewId = (int)args[0];
                string key = args[1] as string;
                bool active = (bool)args[2];
                object[] startData = args[3] as object[];
                object[] extended = args[4] as object[];
                bool lateJoin = (bool)args[5];
                if (key == null) return;

                PhotonView view = PhotonView.Find(viewId);
                if (view == null) return;   // not instantiated here yet, or already gone

                // Reproduce the PunRPC ownership guarantee: only the view's owner may drive it.
                if (view.Owner == null || sender == null ||
                    view.Owner.ActorNumber != sender.ActorNumber)
                {
                    string tag = viewId + "/" + (sender != null ? sender.ActorNumber : -1);
                    if (ReportedOwnerMismatch.Add(tag))
                        Plugin.Log.LogWarning("Ectype: ignored ability-sync for view " + viewId +
                                              " from a player who does not own it.");
                    return;
                }

                UltimateCharacterLocomotion loco =
                    view.GetComponent<UltimateCharacterLocomotion>();
                Ability ability = RuntimeAbilities.Find(loco, key);
                if (ability == null) return;   // this client has no such ability: nothing to do

                string heldKey = viewId + "/" + key;
                Held.Remove(heldKey);   // a newer message supersedes a held start; a stop cancels it
                if (active && !IsReady(loco))
                {
                    Held[heldKey] = new HeldStart
                    {
                        ViewId = viewId, Key = key, StartData = startData, Extended = extended,
                        LateJoin = lateJoin, Deadline = Time.unscaledTime + HoldSeconds
                    };
                    return;
                }
                Deliver(loco, ability, active, startData, extended, lateJoin);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling ability-sync message failed: " + e);
            }
        }

        /// <summary>
        /// A replica exists before it can run an ability: its locomotion's look source is
        /// attached after spawn, and stock abilities read it unconditionally on start (the
        /// grappling hook's projectile spawn position does).
        /// </summary>
        private static bool IsReady(UltimateCharacterLocomotion loco)
        {
            ILookSource look = loco.LookSource;
            return look != null && !(look is UnityEngine.Object o && o == null);
        }

        private static void Deliver(UltimateCharacterLocomotion loco, Ability ability, bool active,
                                    object[] startData, object[] extended, bool lateJoin)
        {
            if (startData != null && startData.Length > 0)
                ability.SetNetworkStartData(startData);
            IExtendedStartSyncAbility ext = ability as IExtendedStartSyncAbility;
            if (ext != null && extended != null && extended.Length > 0)
                ext.StartData = extended;

            // Same two lines the vanilla receivers execute; the late-join variant uses
            // the flags the vanilla join-time sync uses.
            if (!active) loco.TryStopAbility(ability, true);
            else if (lateJoin) loco.TryStartAbility(ability, true, true);
            else loco.TryStartAbility(ability);
        }

        /// <summary>Driver step: delivers each held start whose character has become ready, and
        /// drops one whose character is gone or that is still held after HoldSeconds.</summary>
        internal static void PollHeld()
        {
            if (Held.Count == 0 || Time.unscaledTime < _nextRetry) return;
            _nextRetry = Time.unscaledTime + RetryInterval;

            foreach (string heldKey in new List<string>(Held.Keys))
            {
                HeldStart h = Held[heldKey];
                try
                {
                    PhotonView view = PhotonView.Find(h.ViewId);
                    UltimateCharacterLocomotion loco =
                        view != null ? view.GetComponent<UltimateCharacterLocomotion>() : null;
                    Ability ability = RuntimeAbilities.Find(loco, h.Key);
                    if (ability == null) { Held.Remove(heldKey); continue; }   // character gone
                    if (IsReady(loco))
                    {
                        Held.Remove(heldKey);
                        Deliver(loco, ability, true, h.StartData, h.Extended, h.LateJoin);
                    }
                    else if (Time.unscaledTime >= h.Deadline)
                    {
                        Held.Remove(heldKey);
                        Plugin.Log.LogWarning("Ectype: dropped ability-sync start '" + h.Key +
                                              "' for view " + h.ViewId + ": the character had " +
                                              "no look source after " + HoldSeconds + " s.");
                    }
                }
                catch (Exception e)
                {
                    Held.Remove(heldKey);
                    Plugin.Log.LogError("Ectype: delivering held ability-sync start '" + h.Key +
                                        "' failed: " + e);
                }
            }
        }
    }


    /// <summary>
    /// Resolves a CustomPunCharacter patch target by name and exact parameter list, logging
    /// and returning null on a miss rather than throwing - a throw out of TargetMethods
    /// aborts the whole PatchAll pass and leaves the framework inert.
    /// </summary>
    internal static class AbilitySyncTargets
    {
        internal static MethodInfo Resolve(string name, Type[] parameters, string consequence)
        {
            try
            {
                Type t = AccessTools.TypeByName("CustomPunCharacter");
                MethodInfo m = t != null ? AccessTools.Method(t, name, parameters) : null;
                if (m == null)
                    Plugin.Log.LogError("Ectype: CustomPunCharacter." + name + " not found - " +
                                        consequence + " (game update?).");
                return m;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: resolving CustomPunCharacter." + name +
                                    " failed - " + consequence + ": " + e);
                return null;
            }
        }
    }

    /// <summary>
    /// Sender. CustomPunCharacter subscribes OnAbilityActive only on the owning client, so
    /// this prefix needs no ownership check of its own and a replica acting on our message
    /// never re-broadcasts. A registered ability is diverted to the framework channel and
    /// the vanilla RPC skipped; everything else falls through, so vanilla traffic is
    /// byte-identical.
    ///
    /// The Opsive PUN add-on assembly is not in the compile-time reference set, so the type
    /// is resolved by name and the view reached through MonoBehaviourPun, which is.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_CustomPunCharacter_OnAbilityActive
    {
        // Yield nothing on a miss: an empty list patches nothing, a null target aborts PatchAll.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo m = AbilitySyncTargets.Resolve("OnAbilityActive",
                new[] { typeof(Ability), typeof(bool) },
                "runtime abilities will sync by array index and can throw on peers");
            if (m != null) yield return m;
        }

        private static bool Prefix(object __instance, Ability ability, bool active)
        {
            try
            {
                string key = RuntimeAbilities.KeyOf(ability);
                if (key == null) return true;                 // prefab ability: vanilla path
                if (!ability.SyncAbilityState) return false;  // vanilla would send nothing

                MonoBehaviourPun pun = __instance as MonoBehaviourPun;
                PhotonView view = pun != null ? pun.photonView : null;
                if (view == null) return false;

                RuntimeAbilities.Send(RuntimeAbilities.Recipients(), view.ViewID, key, active,
                                      null, RuntimeAbilities.ExtendedStartData(ability), false);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: ability-sync send failed: " + e);
                return false;   // never fall back to the index RPC for a registered ability
            }
        }
    }

    /// <summary>
    /// Late-join sync. The vanilla OnPlayerEnteredRoom sends a joiner the owner's currently
    /// active abilities by index (and, in this game's override, only the ones implementing
    /// IExtendedStartSyncAbility). Registered abilities are not on that path, so they are
    /// sent here, to the joiner alone, with the same start data the vanilla path carries.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_CustomPunCharacter_OnPlayerEnteredRoom
    {
        private static readonly FieldInfo LocomotionF = AccessTools.Field(
            AccessTools.TypeByName(
                "Opsive.UltimateCharacterController.AddOns.Multiplayer.PhotonPun.Character.PunCharacter"),
            "m_CharacterLocomotion");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            // Parameter types are required: MonoBehaviourPunCallbacks up the chain declares
            // its own OnPlayerEnteredRoom(Player), so a by-name lookup is ambiguous.
            MethodInfo m = AbilitySyncTargets.Resolve("OnPlayerEnteredRoom",
                new[] { typeof(Player), typeof(GameObject) },
                "late joiners will not learn running runtime abilities");
            if (m != null) yield return m;
        }

        private static void Postfix(object __instance, Player player)
        {
            try
            {
                if (player == null || LocomotionF == null) return;
                if (!PeerPresence.HasFramework(player)) return;

                MonoBehaviourPun pun = __instance as MonoBehaviourPun;
                PhotonView view = pun != null ? pun.photonView : null;
                UltimateCharacterLocomotion loco =
                    LocomotionF.GetValue(__instance) as UltimateCharacterLocomotion;
                if (view == null || loco == null || loco.ActiveAbilities == null) return;

                Player[] to = { player };
                for (int i = 0; i < loco.ActiveAbilityCount && i < loco.ActiveAbilities.Length; i++)
                {
                    Ability a = loco.ActiveAbilities[i];
                    string key = RuntimeAbilities.KeyOf(a);
                    if (key == null || !a.SyncAbilityState) continue;
                    RuntimeAbilities.Send(to, view.ViewID, key, true,
                                          a.GetNetworkStartData(),
                                          RuntimeAbilities.ExtendedStartData(a), true);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: late-join ability-sync failed: " + e);
            }
        }
    }

    /// <summary>
    /// Which peers run a framework that speaks this build's network contract. Every running
    /// framework publishes the Photon player property <see cref="PropertyKey"/> on its local
    /// player, carrying <see cref="EctypePluginInfo.NET_CONTRACT_VERSION"/>. Ability
    /// replication and enemy-status replication go only to peers whose property matches,
    /// since a peer without it has no handler for the framework's event.
    ///
    /// The property is set on the local player before any room is joined. PUN keeps a local
    /// player's custom properties across rooms and sends them with every join request, so the
    /// property is already on the joiner when other clients see the join, and a crewmate's
    /// late-join sync can address the joiner immediately.
    /// </summary>
    internal static class PeerPresence
    {
        /// <summary>The Photon player property naming the framework's network contract.</summary>
        internal const string PropertyKey = "Ectype";

        private const float PublishInterval = 1f;
        private static float _nextPublish;

        /// <summary>Every other player in the current room whose framework speaks this
        /// build's network contract. Empty in solo or outside a room.</summary>
        internal static List<Player> PeersWithFramework()
        {
            List<Player> with = new List<Player>();
            Player[] others = PhotonNetwork.PlayerListOthers;
            if (others == null) return with;
            foreach (Player p in others)
                if (HasFramework(p)) with.Add(p);
            return with;
        }

        private static readonly HashSet<string> ReportedMismatch = new HashSet<string>();

        /// <summary>The contract number a player published, or null when it published none.</summary>
        internal static int? ContractOf(Player player)
        {
            object v;
            if (player == null || player.CustomProperties == null ||
                !player.CustomProperties.TryGetValue(PropertyKey, out v) || !(v is int)) return null;
            return (int)v;
        }

        /// <summary>
        /// False when the player published a different network contract: such a peer is never
        /// a recipient and its messages are dropped. Logs once per player and contract number.
        /// A player with no property is not judged here.
        /// </summary>
        internal static bool ContractCompatible(Player player)
        {
            int? theirs = ContractOf(player);
            if (!theirs.HasValue || theirs.Value == EctypePluginInfo.NET_CONTRACT_VERSION)
                return true;
            if (ReportedMismatch.Add(player.ActorNumber + "/" + theirs.Value))
                Plugin.Log.LogWarning("Ectype: player '" + player.NickName + "' (actor " +
                                      player.ActorNumber + ") runs a framework on network " +
                                      "contract " + theirs.Value + ", this one speaks " +
                                      EctypePluginInfo.NET_CONTRACT_VERSION + ": no ability or " +
                                      "status messages are exchanged with them. Install the " +
                                      "same framework version on both sides.");
            return false;
        }

        /// <summary>Whether a player published this build's network contract.</summary>
        internal static bool HasFramework(Player player)
        {
            if (player == null || player.CustomProperties == null) return false;
            object v;
            return player.CustomProperties.TryGetValue(PropertyKey, out v) && v is int &&
                   (int)v == EctypePluginInfo.NET_CONTRACT_VERSION;
        }

        /// <summary>
        /// Driver step: sets the property on the local player when it is missing or carries
        /// another value. Outside a room the write stays local and rides the next join; in a
        /// room it is sent to the room. Checked once a second.
        /// </summary>
        internal static void Publish()
        {
            if (UnityEngine.Time.unscaledTime < _nextPublish) return;
            _nextPublish = UnityEngine.Time.unscaledTime + PublishInterval;

            foreach (Player p in PhotonNetwork.PlayerListOthers) ContractCompatible(p);

            Player me = PhotonNetwork.LocalPlayer;
            if (me == null || HasFramework(me)) return;
            me.SetCustomProperties(new Hashtable
            {
                { PropertyKey, EctypePluginInfo.NET_CONTRACT_VERSION }
            });
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: published player property '" + PropertyKey + "' = " +
                                   EctypePluginInfo.NET_CONTRACT_VERSION + (PhotonNetwork.InRoom
                                       ? " to the room." : " for the next join."));
        }
    }
}
