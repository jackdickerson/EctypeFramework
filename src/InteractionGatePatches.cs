using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Client.Player.Interactions;
using CG.Client.Ship;
using CG.Client.Ship.Interactions;
using CG.Client.Ship.Modules.Mediators;
using CG.Game.Player;
using CG.Game.SpaceObjects.Controllers;
using CG.GameLoopStateMachine;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using CG.Ship.Repair;
using ExitGames.Client.Photon;
using Gameplay.Carryables;
using Gameplay.Defects;
using Gameplay.Enhancements;
using Gameplay.Power;
using Gameplay.Terminals;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using RSG;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// The interaction gate's chokepoints. Every hook returns at once while no gate is
    /// registered, and never changes the outcome when every gate allows, so another mod's
    /// patches on the same methods keep their effect: either mod refusing refuses. The hooks run
    /// after Void Crew Fleet's on the same methods and stay silent when Fleet has already refused.
    ///
    /// Client side (the acting player's own game, for the prompt and the warning): the
    /// interaction raycast's final pick, each station's own entry (seat, terminals, repairs and
    /// defects), the activation event for the stations that raise nothing else, the carry
    /// interaction and its pick-up, and the module upgrade tool.
    ///
    /// Owner side (the part owner's game, for authority): each network request a player's game
    /// sends the owner of a part. The sender of most of the game's requests is recovered from
    /// <see cref="RpcSender"/>; the rest carry it. Requests from the local player and from the
    /// host are never refused there: the host's game logic sends requests of its own, and the
    /// host's own actions are judged on its client side.
    ///
    /// The Photon hooks (the RPC sender, the socket request context and the ownership events)
    /// are patched from the driver once a gate is registered and the game's state machine has
    /// left its loading states:
    /// patching PhotonNetwork at start-up sets Photon up before the game does, and its
    /// connection then never gets past the name server.
    /// </summary>
    internal static class InteractionGatePatches
    {
        /// <summary>The game's default reach (RaycastHandler's private 2.3 m): a refused hover
        /// warns only this close.</summary>
        private const float WarnDistance = 2.3f;

        // The game's requests to a carrier's owner: put an item in, let it out, throw it out.
        private const byte InsertRequestEvent = 3;
        private const byte ReleaseRequestEvent = 4;
        private const byte EjectRequestEvent = 5;
        // PUN's ownership events: a player takes a network object, or one is handed to a player.
        private const byte OwnershipRequestEvent = 209;
        private const byte OwnershipTransferEvent = 210;
        // PUN's ownership update: sets owners on any objects. The game's host alone sends it.
        private const byte OwnershipUpdateEvent = 212;

        private static Harmony _harmony;
        private static bool _photonPatched;
        private static AccessTools.FieldRef<TakeoverChair, bool> _chairInUse;

        /// <summary>The socket request event Photon is dispatching right now, or null.</summary>
        private static EventData _socketEvent;

        private static HarmonyMethod Hook(string name)
        {
            HarmonyMethod m = new HarmonyMethod(typeof(InteractionGatePatches), name);
            m.after = new[] { Topology.FleetPluginGuid };
            return m;
        }

        /// <summary>Patches the client and owner chokepoints that do not touch Photon's own
        /// classes. Each is resolved on its own, so a renamed member costs only its hook.</summary>
        internal static void ApplyPatches(Harmony harmony)
        {
            _harmony = harmony;
            try { _chairInUse = AccessTools.FieldRefAccess<TakeoverChair, bool>("isUsing"); }
            catch (Exception)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: TakeoverChair.isUsing not found (game update?); " +
                                      "a seated player is judged again when the game re-asks after a host change.");
            }
            int ok = 0, total = 0;

            // Client side.
            Patch(ref ok, ref total, typeof(RaycastHandler), "FindInteractable", null, null, "FindInteractablePostfix",
                  "refused interactables still show their prompt");
            Patch(ref ok, ref total, typeof(CustomCharacterLocomotion), "OnActivateAbilities", null,
                  "ActivateAbilitiesPrefix", null, "clone tubes and the periscope start without a client check");
            Patch(ref ok, ref total, typeof(TakeoverChair), "TrySitInChair", Type.EmptyTypes, "TrySitPrefix", null,
                  "seats are taken without a client check");
            Patch(ref ok, ref total, typeof(TerminalModuleMediator), "OpenTerminalUi", Type.EmptyTypes,
                  "TerminalOpenPrefix", null, "module terminals open without a client check");
            Patch(ref ok, ref total, typeof(TerminalScreen), "OnInteractClicked", Type.EmptyTypes,
                  "TerminalScreenPrefix", null, "terminal screens open without a client check");
            Patch(ref ok, ref total, typeof(RepairObject), "ClickedRepairStart", Type.EmptyTypes, "RepairStartPrefix", null,
                  "repairs start without a client check");
            Patch(ref ok, ref total, typeof(Defect), "StartDefectRepair", Type.EmptyTypes, "DefectStartPrefix", null,
                  "defect repairs start without a client check");
            Patch(ref ok, ref total, typeof(CarryableInteract), "StartInteraction",
                  new[] { typeof(AbstractInteractable), typeof(bool) }, "StartInteractionPrefix", null,
                  "pick-ups and socket use have no client backstop");
            Patch(ref ok, ref total, typeof(CarryableInteract), "PickupGrabable",
                  new[] { typeof(AbstractInteractable) }, "PickupPrefix", null,
                  "the pick-up that follows a socket swap has no client check");
            Patch(ref ok, ref total, typeof(InteractiveCarryableModuleUpgrader), "IsInvalidUpgradeTarget", null, null,
                  "UpgradeTargetPostfix", "the module upgrade tool is not gated");

            // Owner side.
            Patch(ref ok, ref total, typeof(TakeoverChair), "RequestSitInChair", null, "SeatRequestPrefix", null,
                  "seat requests are not refused by the owner");
            Patch(ref ok, ref total, typeof(Carrier), "OnOwnershipRequest", null, "CarrierOwnershipPrefix", null,
                  "requests for a carried or socketed item are not refused by the owner");
            Patch(ref ok, ref total, typeof(CarrierCarryableHandler), "TryInsertCarryable", null, "SocketInsertPrefix", null,
                  "socket inserts are not refused by the owner");
            Patch(ref ok, ref total, typeof(CarrierCarryableHandler), "TryReleaseCarryable", null, "SocketReleasePrefix", null,
                  "socket releases are not refused by the owner");
            Patch(ref ok, ref total, typeof(CarrierCarryableHandler), "TryEjectCarryable", null, "SocketEjectPrefix", null,
                  "socket ejects are not refused by the owner");
            foreach (string m in new[] { "RequestBool", "RequestByte", "RequestInt" })
                Patch(ref ok, ref total, typeof(ChangeResponsivePun), m, null, "SyncedValuePrefix", null,
                      "synced switches are not refused by the owner");
            Patch(ref ok, ref total, typeof(PowerDrain), "RequestSetPowerState", null, "PowerRequestPrefix", null,
                  "system power requests are not refused by the owner");
            Patch(ref ok, ref total, typeof(PowerPropagator), "RequestSetPowerState", null, "PowerRequestPrefix", null,
                  "power line requests are not refused by the owner");
            Patch(ref ok, ref total, typeof(PowerSystem), "RequestPowerChange", null, "PowerRequestPrefix", null,
                  "ship power requests are not refused by the owner");
            Patch(ref ok, ref total, typeof(Enhancement), "StateChangePun", null, "PanelStatePrefix", null,
                  "panel state requests are not refused by the owner");
            Patch(ref ok, ref total, typeof(DoorController), "RequestOpenOnMaster", null, "DoorRequestPrefix", null,
                  "door requests are not refused by the host");
            Patch(ref ok, ref total, typeof(AirlockController), "NetworkAirlockOpenRequest", null, "AirlockDoorPrefix", null,
                  "airlock door requests are not refused by the host");
            Patch(ref ok, ref total, typeof(AirlockController), "NetworkStartPressurization", null, "AirlockControlPrefix", null,
                  "airlock cycling is not refused by the host");
            Patch(ref ok, ref total, typeof(AirlockController), "NetworkSafetyRequest", null, "AirlockControlPrefix", null,
                  "the airlock safety override is not refused by the host");

            Plugin.Log.LogInfo("Ectype: interaction gate: " + ok + " of " + total + " hooks patched; the " +
                               "Photon hooks wait for the game to load.");
        }

        private static void Patch(ref int ok, ref int total, Type type, string method, Type[] args, string prefix,
                                  string postfix, string consequence)
        {
            total++;
            try
            {
                MethodInfo target = args != null
                    ? AccessTools.DeclaredMethod(type, method, args)
                    : AccessTools.DeclaredMethod(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning("Ectype: interaction gate: " + type.Name + "." + method +
                                          " not found (game update?); " + consequence + ".");
                    return;
                }
                _harmony.Patch(target, prefix: prefix != null ? Hook(prefix) : null,
                               postfix: postfix != null ? Hook(postfix) : null);
                ok++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: " + type.Name + "." + method +
                                      " could not be patched; " + consequence + ": " + e.Message);
            }
        }

        /// <summary>
        /// Driver step. Prunes the classifier's registrations of destroyed controls on a slow
        /// timer, and patches the Photon hooks once, when a gate is registered and the game's
        /// state machine has left its first state and the loading screen. With no gate ever
        /// registered, Photon is never patched.
        /// </summary>
        internal static void PollLate()
        {
            InteractionClassifier.PollPrune();
            PollHandBacks();
#if ECTYPE_DEV
            PollPickupCheck();
#endif
            if (_photonPatched || _harmony == null || !InteractionGate.AnyRegistered || !GameLoaded()) return;
            _photonPatched = true;
            int ok = 0, total = 0;
            PatchPhoton(ref ok, ref total, typeof(PhotonNetwork), "ExecuteRpc", typeof(RpcSender), "RpcPrefix", "RpcFinalizer",
                        "power, panel and synced-switch requests are not refused by the owner");
            PatchPhoton(ref ok, ref total, typeof(LoadBalancingClient), "OnEvent", typeof(InteractionGatePatches),
                        "DispatchPrefix", "DispatchFinalizer", "socket requests are not refused by the owner");
            PatchPhoton(ref ok, ref total, typeof(PhotonNetwork), "OnEvent", typeof(InteractionGatePatches),
                        "OwnershipEventPrefix", null, "ownership takeovers are not refused");
            try { PhotonNetwork.AddCallbackTarget(OwnershipFailures.Instance); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: the ownership failure listener could not be " +
                                      "added; a grab that loses a race stays locked as in the game: " + e.Message);
            }
            Plugin.Log.LogInfo("Ectype: interaction gate: " + ok + " of " + total + " Photon hooks patched " +
                               "after the game loaded.");
        }

        private static bool GameLoaded()
        {
            try
            {
                GameStateMachine machine = GameStateMachine.Instance;
                if (machine == null || machine.CurrentState == null) return false;
                // Compared by name, so a renamed state cannot break the check.
                string state = machine.CurrentState.GetType().Name;
                return state != "GSNull" && state != "GSLoading";
            }
            catch (Exception) { return false; }
        }

        private static void PatchPhoton(ref int ok, ref int total, Type type, string method, Type hooks,
                                        string prefix, string finalizer, string consequence)
        {
            total++;
            try
            {
                MethodInfo target = AccessTools.DeclaredMethod(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning("Ectype: interaction gate: " + type.Name + "." + method +
                                          " not found (Photon update?); " + consequence + ".");
                    return;
                }
                HarmonyMethod pre = new HarmonyMethod(hooks, prefix);
                pre.after = new[] { Topology.FleetPluginGuid };
                HarmonyMethod fin = finalizer != null ? new HarmonyMethod(hooks, finalizer) : null;
                _harmony.Patch(target, prefix: pre, finalizer: fin);
                ok++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: " + type.Name + "." + method +
                                      " could not be patched; " + consequence + ": " + e.Message);
            }
        }

        // ---- shared helpers ----------------------------------------------------------------

        private static bool LocalCarrying()
        {
            try
            {
                LocalPlayer p = LocalPlayer.Instance;
                return p != null && p.Carrier != null && p.Payload != null;
            }
            catch (Exception) { return false; }
        }

        private static GateQuery ClientQuery(Component interactable, InteractionKind kind)
        {
            return new GateQuery(PhotonNetwork.LocalPlayer, interactable, kind, GateSide.Client);
        }

        /// <summary>
        /// The client-side verdict on an interactable, classified by the shared classifier. A
        /// swap at an occupied socket puts the carried item in, takes the socket's item out and
        /// picks that item up, and the owner judges each of those requests, so all three are
        /// judged here before anything is sent.
        /// </summary>
        private static bool RefuseInteractable(Component interactable, bool warn)
        {
            Component part = InteractionClassifier.Canonical(interactable);
            InteractionKind kind = InteractionClassifier.Classify(part, LocalCarrying());
            if (InteractionGate.RefuseOnClient(ClientQuery(part, kind), warn)) return true;
            if (kind != InteractionKind.SocketInsert) return false;
            CarryablesSocket socket = part as CarryablesSocket;
            CarryableObject held = socket != null ? socket.Payload : null;
            if (held == null) return false;
            // The old item is loose by the time the game picks it up, so it is presented as itself.
            return InteractionGate.RefuseOnClient(ClientQuery(part, InteractionKind.SocketRelease), warn) ||
                   InteractionGate.RefuseOnClient(ClientQuery(held, InteractionKind.CarryablePickup), warn);
        }

        /// <summary>True when an owner-side check applies to this sender: a known remote player
        /// who is not the host.</summary>
        private static bool Judged(Photon.Realtime.Player sender)
        {
            return sender != null && !sender.IsLocal && !sender.IsMasterClient;
        }

        private static Photon.Realtime.Player PlayerOf(int actor)
        {
            Room room = PhotonNetwork.CurrentRoom;
            return room != null && actor > 0 ? room.GetPlayer(actor) : null;
        }

        private static bool RefuseRequest(Photon.Realtime.Player sender, Component part, InteractionKind kind)
        {
            if (!Judged(sender) || part == null) return false;
            return InteractionGate.RefuseOnOwner(new GateQuery(sender, part, kind, GateSide.Owner));
        }

        // ---- client side -------------------------------------------------------------------

        /// <summary>
        /// The raycast's final pick, after the game resolved occlusion and turned an item in a
        /// socket into the socket: a refused pick is no pick at all (no highlight, no prompt,
        /// no intersection point), with a warning when it is within the game's default reach.
        /// One verdict per frame, on what the player actually looks at.
        /// </summary>
        private static void FindInteractablePostfix(ref AbstractInteractable interactable, ref Vector3? worldPos,
                                                    Transform ___cameraTransform)
        {
            if (!InteractionGate.AnyRegistered || interactable == null) return;
            try
            {
                float distance = worldPos.HasValue && ___cameraTransform != null
                    ? Vector3.Distance(worldPos.Value, ___cameraTransform.position) : 0f;
                if (!RefuseInteractable(interactable, distance < WarnDistance)) return;
                interactable = null;
                worldPos = null;
            }
            catch (Exception e) { InteractionClassifier.LogHookFailure("RaycastHandler.FindInteractable", e); }
        }

        /// <summary>
        /// Every station the local player starts raises one event on their character. A clone
        /// tube and the periscope raise nothing else, so they are judged here, along with any
        /// station a mod adds. The others change state before or after raising it, and are judged
        /// at their own entry instead, before anything changes: a seat (and the helm or turret it
        /// starts) at <see cref="TrySitPrefix"/>, a terminal at <see cref="TerminalOpenPrefix"/>
        /// and <see cref="TerminalScreenPrefix"/>, a repair or defect at
        /// <see cref="RepairStartPrefix"/> and <see cref="DefectStartPrefix"/>, a panel's
        /// minigame at the raycast. Refusing those here would leave a seat taken with the player
        /// standing, the view switched to a terminal the player cannot leave, or a minigame's
        /// skill modifier applied with no minigame running.
        /// </summary>
        private static bool ActivateAbilitiesPrefix(CustomCharacterLocomotion __instance, IAbilityActivator activator,
                                                    bool activate, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || !activate) return true;
            try
            {
                LocalPlayer local = LocalPlayer.Instance;
                if (local == null || __instance == null || !ReferenceEquals(__instance, local.Locomotion)) return true;
                if (activator is TakeoverChair || activator is Helm || activator is ITakeoverModule ||
                    activator is TerminalModule || activator is TerminalScreen || activator is CrypticCore)
                    return true;
                Component c = activator as Component;
                if (c == null) return true;
                return !InteractionGate.RefuseOnClient(ClientQuery(c, InteractionClassifier.Classify(c, false)), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("CustomCharacterLocomotion.OnActivateAbilities", e);
                return true;
            }
        }

        /// <summary>A seat the local player tries to take, before any request is sent or any
        /// state changes. A player already using the seat (the game re-asks after a host change)
        /// is not judged again.</summary>
        private static bool TrySitPrefix(TakeoverChair __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                if (_chairInUse != null && _chairInUse(__instance)) return true;
                return !InteractionGate.RefuseOnClient(ClientQuery(__instance, InteractionClassifier.ChairKind(__instance)), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("TakeoverChair.TrySitInChair", e);
                return true;
            }
        }

        /// <summary>A module terminal the local player opens, before the view changes.</summary>
        private static bool TerminalOpenPrefix(TerminalModuleMediator __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                Component part = __instance.Module != null ? (Component)__instance.Module : __instance;
                return !InteractionGate.RefuseOnClient(ClientQuery(part, InteractionKind.Terminal), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("TerminalModuleMediator.OpenTerminalUi", e);
                return true;
            }
        }

        /// <summary>A world-space terminal screen the local player opens, before the view
        /// changes.</summary>
        private static bool TerminalScreenPrefix(TerminalScreen __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                return !InteractionGate.RefuseOnClient(ClientQuery(__instance, InteractionKind.Terminal), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("TerminalScreen.OnInteractClicked", e);
                return true;
            }
        }

        /// <summary>A repair the local player starts, before the instant-win roll and before the
        /// repair skill modifier is applied. A minigame already running is not interrupted.</summary>
        private static bool RepairStartPrefix(RepairObject __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                return !InteractionGate.RefuseOnClient(ClientQuery(__instance, InteractionKind.Minigame), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("RepairObject.ClickedRepairStart", e);
                return true;
            }
        }

        /// <summary>A defect repair the local player starts, before the instant-win roll.</summary>
        private static bool DefectStartPrefix(Defect __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                return !InteractionGate.RefuseOnClient(ClientQuery(__instance, InteractionKind.Minigame), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("Defect.StartDefectRepair", e);
                return true;
            }
        }

        /// <summary>Backstop for the carry interaction: picking up, putting into a socket and
        /// taking out of one, on whatever the raycast offered.</summary>
        private static bool StartInteractionPrefix(AbstractInteractable interactable, LocalPlayer ___player,
                                                   bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || interactable == null) return true;
            try
            {
                if (___player == null || ___player != LocalPlayer.Instance) return true;
                return !RefuseInteractable(interactable, true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("CarryableInteract.StartInteraction", e);
                return true;
            }
        }

        /// <summary>
        /// The pick-up itself, run a frame after the click and also after a socket swap, which
        /// picks the socket's old item up with no click of its own. A refusal here comes before
        /// the ownership request, and clears the carry lock the caller set, so the player can
        /// carry again.
        /// </summary>
        private static bool PickupPrefix(AbstractInteractable interactable, LocalPlayer ___player,
                                         ref bool ___lockInteraction, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || interactable == null) return true;
            try
            {
                if (___player == null || ___player != LocalPlayer.Instance) return true;
                if (!RefuseInteractable(interactable, true)) return true;
                ___lockInteraction = false;
                if (___player.Carrier != null) ___player.Carrier.OverrideSentPayloadToNull = false;
                NoteRefusedPickup();
                return false;
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("CarryableInteract.PickupGrabable", e);
                return true;
            }
        }

        /// <summary>The module upgrade tool aims by itself, without the raycast: a refused
        /// module is an invalid target, which stops the tool.</summary>
        private static void UpgradeTargetPostfix(InteractiveCarryableModuleUpgrader __instance, CellModule module,
                                                 ref bool __result)
        {
            if (!InteractionGate.AnyRegistered || __result || module == null) return;
            try
            {
                LocalPlayer local = LocalPlayer.Instance;
                if (local == null || !ReferenceEquals(__instance.Carrier, local)) return;
                if (InteractionGate.RefuseOnClient(ClientQuery(module, InteractionKind.ModuleUpgrade), true))
                    __result = true;
            }
            catch (Exception e) { InteractionClassifier.LogHookFailure("InteractiveCarryableModuleUpgrader.IsInvalidUpgradeTarget", e); }
        }

        // ---- owner side --------------------------------------------------------------------

        /// <summary>The seat's owner (the host while it is free) hands it to whoever asks. A
        /// refused request is dropped, as the game drops one itself while a seat's module is
        /// being rebuilt.</summary>
        private static bool SeatRequestPrefix(TakeoverChair __instance, PhotonMessageInfo info, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered) return true;
            try
            {
                return !RefuseRequest(info.Sender, __instance, InteractionClassifier.ChairKind(__instance));
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("TakeoverChair.RequestSitInChair", e);
                return true;
            }
        }

        /// <summary>
        /// Every carrier hears every ownership request for a request-only network object; only
        /// the one holding the requested item, on its owner's game, hands it over. That one is
        /// judged: an item in a socket as taking it out, any other as a pick-up.
        /// </summary>
        private static bool CarrierOwnershipPrefix(Carrier __instance, PhotonView targetView,
                                                   Photon.Realtime.Player requestingPlayer, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || targetView == null || !Judged(requestingPlayer)) return true;
            try
            {
                CarryableObject item = __instance.Payload;
                if (!targetView.IsMine || item == null || item.ViewID != targetView.ViewID) return true;
                Component socket = __instance.Owner as CarryablesSocket;
                bool refused = socket != null
                    ? RefuseRequest(requestingPlayer, socket, InteractionKind.SocketRelease)
                    : RefuseRequest(requestingPlayer, item, InteractionKind.CarryablePickup);
                return !refused;
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("Carrier.OnOwnershipRequest", e);
                return true;
            }
        }

        private static bool SocketInsertPrefix(PhotonView ___photonView, ICarrierComponent ___carrier, ref IPromise __result,
                                               bool __runOriginal)
        {
            return SocketRequest(InsertRequestEvent, InteractionKind.SocketInsert, ___photonView, ___carrier,
                                 ref __result, __runOriginal);
        }

        private static bool SocketReleasePrefix(PhotonView ___photonView, ICarrierComponent ___carrier, ref IPromise __result,
                                                bool __runOriginal)
        {
            return SocketRequest(ReleaseRequestEvent, InteractionKind.SocketRelease, ___photonView, ___carrier,
                                 ref __result, __runOriginal);
        }

        private static bool SocketEjectPrefix(PhotonView ___photonView, ICarrierComponent ___carrier, ref IPromise __result,
                                              bool __runOriginal)
        {
            return SocketRequest(EjectRequestEvent, InteractionKind.SocketRelease, ___photonView, ___carrier,
                                 ref __result, __runOriginal);
        }

        /// <summary>
        /// A player's game asks the owner of a carrier, by a Photon event the carrier answers while
        /// it is being dispatched, to put an item in, let one out, or throw one out. A refusal is
        /// a rejected promise, which the game turns into its own failure reply to the asker, the
        /// same reply as for an occupied socket.
        /// </summary>
        private static bool SocketRequest(byte code, InteractionKind kind, PhotonView view, ICarrierComponent carrier,
                                          ref IPromise result, bool runOriginal)
        {
            if (!runOriginal) return false;
            EventData ev = _socketEvent;
            if (ev == null || ev.Code != code || view == null || !InteractionGate.AnyRegistered) return true;
            try
            {
                object[] data = ev.CustomData as object[];
                if (data == null || data.Length == 0 || !(data[0] is int) || (int)data[0] != view.ViewID) return true;
                Carrier c = carrier as Carrier;
                Component part = c != null && c.Owner is Component ? (Component)c.Owner : (Component)c;
                if (!RefuseRequest(PlayerOf(ev.Sender), part, kind)) return true;
                Promise refused = new Promise();
                refused.Reject(new Exception("refused by an interaction gate"));
                result = refused;
                return false;
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("CarrierCarryableHandler socket request", e);
                return true;
            }
        }

        /// <summary>Any synced switch or setting on a part (breakers, module settings, a
        /// panel's switch, a power switch), judged as the component that holds it.</summary>
        private static bool SyncedValuePrefix(ChangeResponsivePun __instance, int index,
                                              ChangeResponsivePun.ChangeResponsiveWrapper[] ___ChangeResponsives,
                                              bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered) return true;
            try
            {
                Photon.Realtime.Player sender;
                if (!RpcSender.TryGet(__instance.photonView, out sender)) return true;
                ChangeResponsivePun.ChangeResponsiveWrapper w =
                    ___ChangeResponsives != null && index >= 0 && index < ___ChangeResponsives.Length
                        ? ___ChangeResponsives[index] : null;
                Component holder = w != null && w.changeResponsive != null ? w.changeResponsive.Component : null;
                if (holder == null) holder = __instance;
                return !RefuseRequest(sender, holder, InteractionClassifier.Classify(holder, false));
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("ChangeResponsivePun request", e);
                return true;
            }
        }

        /// <summary>A system's, a power line's or the whole ship's power switch.</summary>
        private static bool PowerRequestPrefix(MonoBehaviourPun __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                Photon.Realtime.Player sender;
                if (!RpcSender.TryGet(__instance.photonView, out sender)) return true;
                return !RefuseRequest(sender, __instance, InteractionKind.PowerSwitch);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("power request", e);
                return true;
            }
        }

        /// <summary>A System Enhancement panel's state change, asked of its owner.</summary>
        private static bool PanelStatePrefix(Enhancement __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                Photon.Realtime.Player sender;
                if (!RpcSender.TryGet(__instance.photonView, out sender)) return true;
                return !RefuseRequest(sender, __instance, InteractionKind.EnhancementPanel);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("Enhancement.StateChangePun", e);
                return true;
            }
        }

        /// <summary>A door the host is asked to open or close. A refusal answers the asker with
        /// the door's real state, which undoes the asker's own early swing.</summary>
        private static bool DoorRequestPrefix(DoorController __instance, int index, PhotonMessageInfo info,
                                              List<AbstractDoor> ____shipDoors, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered) return true;
            try
            {
                AbstractDoor door = ____shipDoors != null && index >= 0 && index < ____shipDoors.Count
                    ? ____shipDoors[index] : null;
                if (door == null) return true;
                if (!RefuseRequest(info.Sender, door, InteractionKind.Door)) return true;
                PhotonView view = __instance.GetComponent<PhotonView>();
                if (view != null) view.RPC("NotifyClientOfOutcome", info.Sender, door.IsOpen, index);
                return false;
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("DoorController.RequestOpenOnMaster", e);
                return true;
            }
        }

        private static bool AirlockDoorPrefix(int index, PhotonMessageInfo info, List<Airlock> ___airlocks,
                                              bool __runOriginal)
        {
            return AirlockRequest(index, info, ___airlocks, InteractionKind.Door, __runOriginal);
        }

        private static bool AirlockControlPrefix(int index, PhotonMessageInfo info, List<Airlock> ___airlocks,
                                                 bool __runOriginal)
        {
            return AirlockRequest(index, info, ___airlocks, InteractionKind.AirlockControl, __runOriginal);
        }

        /// <summary>An airlock's door, cycle or safety override, asked of the host.</summary>
        private static bool AirlockRequest(int index, PhotonMessageInfo info, List<Airlock> airlocks,
                                           InteractionKind kind, bool runOriginal)
        {
            if (!runOriginal) return false;
            if (!InteractionGate.AnyRegistered) return true;
            try
            {
                Airlock airlock = airlocks != null && index >= 0 && index < airlocks.Count ? airlocks[index] : null;
                return airlock == null || !RefuseRequest(info.Sender, airlock, kind);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("AirlockController request", e);
                return true;
            }
        }

        // ---- Photon: socket request context and ownership events -------------------------

        /// <summary>Photon hands each event to PUN's handler and then to every carrier, all
        /// inside this one dispatch. A socket request is held for the carriers' answer.</summary>
        private static void DispatchPrefix(EventData photonEvent, out EventData __state)
        {
            __state = _socketEvent;
            byte code = photonEvent != null ? photonEvent.Code : (byte)0;
            _socketEvent = code >= InsertRequestEvent && code <= EjectRequestEvent ? photonEvent : null;
        }

        private static void DispatchFinalizer(EventData __state)
        {
            _socketEvent = __state;
        }

        /// <summary>
        /// Ownership events. A network object set to be taken over at will changes owner on each
        /// client by itself when a player takes it (209) or is handed it (210), and an ownership
        /// update (212) sets owners on any object from any sender. The object's current owner
        /// decides, or the host for an object with no owner present:
        /// - it ignores a refused change and hands the object back to itself, which every other
        ///   client applies after the refused change, so the room converges on the owner;
        /// - the taker's own game also judges its own 209 and, on a refusal, answers the game's
        ///   pending pick-up as a failure, so its carry interaction is not left locked, and hands
        ///   the object back to the owner it still sees, so every client converges on the previous
        ///   owner even where the deciding side allowed the change. Either judge refusing returns
        ///   the object to its previous owner.
        /// Every other client applies the event as PUN does. Not judged: the host as taker or new
        /// owner, a hand-off by the host or the current owner (the game's own logic), an object
        /// following its holder (a socket's item to the socket's owner, a seat's connected object
        /// to the seated player), an item a player carries, and anything outside the ship and its
        /// items.
        /// </summary>
        private static bool OwnershipEventPrefix(EventData photonEvent, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || photonEvent == null) return true;
            byte code = photonEvent.Code;
            if (code != OwnershipRequestEvent && code != OwnershipTransferEvent && code != OwnershipUpdateEvent)
                return true;
            try
            {
                int[] data = photonEvent.CustomData as int[];
                if (data == null || data.Length < 2) return true;
                if (code == OwnershipUpdateEvent)
                {
                    OwnershipUpdate(photonEvent.Sender, data);
                    return true;
                }
                PhotonView view = PhotonView.Find(data[0]);
                if (view == null) return true;
                return code == OwnershipRequestEvent
                    ? OwnershipRequest(photonEvent.Sender, view)
                    : OwnershipTransfer(photonEvent.Sender, data[1], view);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("PhotonNetwork.OnEvent ownership", e);
                return true;
            }
        }

        /// <summary>209: a player takes an object.</summary>
        private static bool OwnershipRequest(int takerActor, PhotonView view)
        {
            Photon.Realtime.Player taker = PlayerOf(takerActor);
            if (taker == null || taker.IsMasterClient || view.OwnershipTransfer == OwnershipOption.Fixed) return true;
            bool own = taker.IsLocal;
            // A request-only object is decided by its owner through the request hooks.
            bool decides = view.OwnershipTransfer == OwnershipOption.Takeover && Decides(view);
            if (!own && !decides) return true;
            Component part;
            InteractionKind kind;
            if (!OwnershipTarget(view, out part, out kind)) return true;
            if (own)
            {
                if (!InteractionGate.RefuseOnClient(new GateQuery(taker, part, kind, GateSide.Client), true)) return true;
                SettleLocalPickup(view);
                RememberRefusedOwn(view);
                // Every client that applied this 209 sees the taker as owner, so the hand-back
                // from the taker passes there as the owner's own hand-off.
                if (view.OwnershipTransfer == OwnershipOption.Takeover) view.TransferOwnership(view.OwnerActorNr);
                return false;
            }
            if (!InteractionGate.RefuseOnOwner(new GateQuery(taker, part, kind, GateSide.Owner))) return true;
            Reassert(view);
            return false;
        }

        /// <summary>210: an object is handed to a player.</summary>
        private static bool OwnershipTransfer(int senderActor, int newOwner, PhotonView view)
        {
            // An object this player refused itself, handed to it anyway by an owner that allowed
            // the request: it is applied, then handed back on the next frame, once this player
            // controls it.
            Photon.Realtime.Player local = PhotonNetwork.LocalPlayer;
            if (local != null && newOwner == local.ActorNumber && RefusedOwnRecently(view))
            {
                HandBacks.Add(new KeyValuePair<PhotonView, int>(view, senderActor));
                return true;
            }
            // PUN applies a request-only object's transfer only from its owner or controller.
            if (view.OwnershipTransfer != OwnershipOption.Takeover) return true;
            if (senderActor == view.OwnerActorNr || senderActor == view.ControllerActorNr) return true;
            // A hand-back to the owner this client already records changes nothing here.
            if (newOwner == view.OwnerActorNr) return true;
            Photon.Realtime.Player sender = PlayerOf(senderActor);
            if (sender != null && sender.IsMasterClient) return true;
            bool refused;
            return !RefuseHandOff(view, newOwner, out refused);
        }

        /// <summary>212 from anyone but the host, which alone sends it in the game: each pair this
        /// client decides is judged as a hand-off. A refused pair is rewritten in place to the
        /// object's current owner, so PUN applies the rest of the update unchanged, and the refused
        /// object is handed back.</summary>
        private static void OwnershipUpdate(int senderActor, int[] pairs)
        {
            Photon.Realtime.Player sender = PlayerOf(senderActor);
            if (sender != null && sender.IsMasterClient) return;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                PhotonView view = PhotonView.Find(pairs[i]);
                if (view == null || pairs[i + 1] == view.OwnerActorNr) continue;
                bool refused;
                RefuseHandOff(view, pairs[i + 1], out refused);
                if (refused) pairs[i + 1] = view.OwnerActorNr;
            }
        }

        /// <summary>Judges handing an object to a player on the client that decides it; a refused
        /// object is handed back to its owner.</summary>
        private static bool RefuseHandOff(PhotonView view, int newOwner, out bool refused)
        {
            refused = false;
            Photon.Realtime.Player to = PlayerOf(newOwner);
            if (to == null || to.IsMasterClient) return false;
            if (!Decides(view) || FollowsHolder(view, newOwner)) return false;
            Component part;
            InteractionKind kind;
            if (!OwnershipTarget(view, out part, out kind)) return false;
            if (!InteractionGate.RefuseOnOwner(new GateQuery(to, part, kind, GateSide.Owner))) return false;
            refused = true;
            Reassert(view);
            return true;
        }

        /// <summary>True when this client decides the object's owner: it owns it, or it is the
        /// host and the object has no owner present in the room.</summary>
        private static bool Decides(PhotonView view)
        {
            Photon.Realtime.Player local = PhotonNetwork.LocalPlayer;
            if (local == null) return false;
            int owner = view.OwnerActorNr;
            if (owner == local.ActorNumber) return true;
            if (!local.IsMasterClient) return false;
            if (owner == 0) return true;
            Photon.Realtime.Player p = PlayerOf(owner);
            return p == null || p.IsInactive;
        }

        /// <summary>The deciding client hands a refused object back: to itself when it owns it,
        /// else (the host, for an object with no owner present) to the room. The server delivers
        /// this after the refused event to every client, so clients that applied the refused
        /// change fall back.</summary>
        private static void Reassert(PhotonView view)
        {
            Photon.Realtime.Player local = PhotonNetwork.LocalPlayer;
            int keep = view.OwnerActorNr == local.ActorNumber || view.OwnerActorNr != 0 ? local.ActorNumber : 0;
            view.TransferOwnership(keep);
        }

        /// <summary>An object following its holder: an item in a socket going to the socket's
        /// owner, or a seat's connected object going to whoever owns the seat.</summary>
        private static bool FollowsHolder(PhotonView view, int newOwner)
        {
            CarryableObject item = view.GetComponentInParent<CarryableObject>();
            if (item != null)
            {
                CarryablesSocket socket = item.Carrier as CarryablesSocket;
                PhotonView socketView = socket != null ? PhotonView.Get(socket) : null;
                return socketView != null && socketView.OwnerActorNr == newOwner;
            }
            TakeoverChair chair = InteractionClassifier.ChairHolding(view);
            return chair != null && chair.photonView != null && chair.photonView.OwnerActorNr == newOwner;
        }

        /// <summary>
        /// The local player's pick-up will not happen: its own game refused it, or PUN refused the
        /// takeover because the item changed hands first. The game's carrier is waiting for the
        /// item's owner change, with nothing else to end the wait; answering it with the unchanged
        /// owner makes the game reject the pick-up itself, which clears the carry lock. With no
        /// pick-up pending the item has no listener and nothing happens.
        /// </summary>
        private static void SettleLocalPickup(PhotonView view)
        {
            CarryableObject item = view != null ? view.GetComponentInParent<CarryableObject>() : null;
            if (item == null || item.OwnerChange == null) return;
            item.OwnerChange(view.Owner);
            NoteRefusedPickup();
        }

        private const float RefusedOwnSeconds = 5f;
        private static readonly Dictionary<int, float> RefusedOwn = new Dictionary<int, float>();
        private static readonly List<KeyValuePair<PhotonView, int>> HandBacks = new List<KeyValuePair<PhotonView, int>>();

        private static void RememberRefusedOwn(PhotonView view)
        {
            if (RefusedOwn.Count > 256) RefusedOwn.Clear();
            RefusedOwn[view.ViewID] = Time.unscaledTime + RefusedOwnSeconds;
        }

        private static bool RefusedOwnRecently(PhotonView view)
        {
            float until;
            return RefusedOwn.TryGetValue(view.ViewID, out until) && Time.unscaledTime < until;
        }

        /// <summary>Driver: hands back the objects an owner gave this player after this player
        /// refused them, now that this player controls them.</summary>
        private static void PollHandBacks()
        {
            if (HandBacks.Count == 0) return;
            KeyValuePair<PhotonView, int>[] due = HandBacks.ToArray();
            HandBacks.Clear();
            foreach (KeyValuePair<PhotonView, int> h in due)
            {
                try
                {
                    if (h.Key != null && h.Key.AmOwner) h.Key.TransferOwnership(h.Value);
                }
                catch (Exception e) { InteractionClassifier.LogHookFailure("ownership hand-back", e); }
            }
        }

        /// <summary>PUN's ownership callbacks: a takeover the local player asked for failed
        /// because the item changed hands first (two players grabbing one item, or a grab during
        /// a refused change's hand-back). The pick-up is settled as a failure.</summary>
        private sealed class OwnershipFailures : IPunOwnershipCallbacks
        {
            internal static readonly OwnershipFailures Instance = new OwnershipFailures();

            public void OnOwnershipRequest(PhotonView targetView, Photon.Realtime.Player requestingPlayer) { }

            public void OnOwnershipTransfered(PhotonView targetView, Photon.Realtime.Player previousOwner) { }

            public void OnOwnershipTransferFailed(PhotonView targetView, Photon.Realtime.Player senderOfFailedRequest)
            {
                try
                {
                    if (senderOfFailedRequest != null && senderOfFailedRequest.IsLocal) SettleLocalPickup(targetView);
                }
                catch (Exception e) { InteractionClassifier.LogHookFailure("ownership transfer failed", e); }
            }
        }

#if ECTYPE_DEV
        private static float _pickupCheckAt = -1f;
        private static AccessTools.FieldRef<CarryableInteract, bool> _carryLock;
#endif

        /// <summary>Dev tools build: schedules the check that a refused pick-up left the carry
        /// interaction unlocked.</summary>
        private static void NoteRefusedPickup()
        {
#if ECTYPE_DEV
            _pickupCheckAt = Time.unscaledTime + 0.5f;
#endif
        }

#if ECTYPE_DEV
        private static void PollPickupCheck()
        {
            if (_pickupCheckAt < 0f || Time.unscaledTime < _pickupCheckAt) return;
            _pickupCheckAt = -1f;
            try
            {
                if (_carryLock == null) _carryLock = AccessTools.FieldRefAccess<CarryableInteract, bool>("lockInteraction");
                LocalPlayer local = LocalPlayer.Instance;
                CarryableInteract carry = local != null && local.Locomotion != null
                    ? local.Locomotion.GetAbility<CarryableInteract>() : null;
                if (carry == null) return;
                if (!_carryLock(carry))
                    Plugin.Log.LogInfo("Ectype: dev check: a refused pick-up left the carry interaction unlocked.");
                else
                    Plugin.Log.LogError("Ectype: dev check: FAILED - the carry interaction is still locked 0.5 s " +
                                        "after a refused pick-up (or a new pick-up started within that time).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: dev check: the refused pick-up check could not run: " + e.Message);
            }
        }
#endif

        private static bool OwnershipTarget(PhotonView view, out Component part, out InteractionKind kind)
        {
            part = null;
            kind = InteractionKind.Other;
            CarryableObject item = view.GetComponentInParent<CarryableObject>();
            if (item != null)
            {
                ICarrier carrier = item.Carrier;
                if (carrier is CG.Game.Player.Player) return false;
                CarryablesSocket socket = carrier as CarryablesSocket;
                if (socket != null)
                {
                    part = socket;
                    kind = InteractionKind.SocketRelease;
                    return true;
                }
                part = item;
                kind = InteractionKind.CarryablePickup;
                return true;
            }
            if (Topology.ShipOfPart(view) == null) return false;
            TakeoverChair chair = view.GetComponent<TakeoverChair>();
            if (chair != null)
            {
                part = chair;
                kind = InteractionClassifier.ChairKind(chair);
                return true;
            }
            CellModule module = view.GetComponent<CellModule>();
            part = module != null ? (Component)module : view;
            kind = InteractionClassifier.Classify(part, false);
            return true;
        }
    }

    /// <summary>
    /// Who sent the Photon RPC that is running now. Most of the game's request RPCs take no
    /// message info, so the method cannot tell who asked. PUN runs every RPC, from the network or
    /// a player's RPC to themselves, inside PhotonNetwork.ExecuteRpc, which is handed the sender
    /// the server stamped on the event and calls the RPC method before it returns. A
    /// prefix and finalizer on it record the sender and the target view for the length of that
    /// call, restoring the outer pair after an RPC run inside another.
    /// </summary>
    internal static class RpcSender
    {
        /// <summary>PUN's key for the target view id in an RPC's data.</summary>
        private static readonly object ViewKey = (byte)0;

        internal struct Frame
        {
            internal Photon.Realtime.Player Sender;
            internal int View;
        }

        private static Frame _current;

        private static void RpcPrefix(ExitGames.Client.Photon.Hashtable rpcData, Photon.Realtime.Player sender,
                                      out Frame __state)
        {
            __state = _current;
            object view = null;
            if (rpcData != null) rpcData.TryGetValue(ViewKey, out view);
            _current = new Frame { Sender = sender, View = view is int ? (int)view : 0 };
        }

        private static void RpcFinalizer(Frame __state)
        {
            _current = __state;
        }

        /// <summary>The sender of the RPC running now on this view; false outside such an RPC,
        /// before the patch is in, or for another view.</summary>
        internal static bool TryGet(PhotonView view, out Photon.Realtime.Player sender)
        {
            sender = null;
            Frame f = _current;
            if (f.Sender == null || view == null || f.View == 0 || f.View != view.ViewID) return false;
            sender = f.Sender;
            return true;
        }
    }
}
