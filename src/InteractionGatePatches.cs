using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Client.Player.Interactions;
using CG.Client.Ship;
using CG.Client.Ship.Modules.Mediators;
using CG.Game.Player;
using CG.Game.SpaceObjects.Controllers;
using CG.GameLoopStateMachine;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using ExitGames.Client.Photon;
using Gameplay.Carryables;
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
    /// interaction raycast, the start of a seat, the one event that starts every station, the
    /// carry interaction and the module upgrade tool.
    ///
    /// Owner side (the part owner's game, for authority): each network request a player's game
    /// sends the owner of a part. The sender of most of the game's requests is recovered from
    /// <see cref="RpcSender"/>; the rest carry it. Requests from the local player and from the
    /// host are never refused there: the host's game logic sends requests of its own, and the
    /// host's own actions are judged on its client side.
    ///
    /// The Photon hooks (the RPC sender, the socket request context and the ownership events)
    /// are patched from the driver once the game's state machine has left its loading states:
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
            Patch(ref ok, ref total, typeof(RaycastHandler), "IsValidInteractableHit", null, null, "ValidHitPostfix",
                  "interactables are not hidden from the prompt");
            Patch(ref ok, ref total, typeof(RaycastHandler), "FindInteractable", null, null, "FindInteractablePostfix",
                  "an item in a socket is not hidden from the prompt");
            Patch(ref ok, ref total, typeof(CustomCharacterLocomotion), "OnActivateAbilities", null,
                  "ActivateAbilitiesPrefix", null, "clone tubes and the periscope start without a client check");
            Patch(ref ok, ref total, typeof(TakeoverChair), "TrySitInChair", Type.EmptyTypes, "TrySitPrefix", null,
                  "seats are taken without a client check");
            Patch(ref ok, ref total, typeof(TerminalModuleMediator), "OpenTerminalUi", Type.EmptyTypes,
                  "TerminalOpenPrefix", null, "module terminals open without a client check");
            Patch(ref ok, ref total, typeof(TerminalScreen), "OnInteractClicked", Type.EmptyTypes,
                  "TerminalScreenPrefix", null, "terminal screens open without a client check");
            Patch(ref ok, ref total, typeof(CrypticCore), "Activate", Type.EmptyTypes, "MinigamePrefix", null,
                  "minigames start without a client check");
            Patch(ref ok, ref total, typeof(CarryableInteract), "StartInteraction",
                  new[] { typeof(AbstractInteractable), typeof(bool) }, "StartInteractionPrefix", null,
                  "pick-ups and socket use have no client backstop");
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
        /// Driver step: once the game's state machine has left its first state and the loading
        /// screen, patches the Photon hooks, once.
        /// </summary>
        internal static void PollLate()
        {
            if (_photonPatched || _harmony == null || !GameLoaded()) return;
            _photonPatched = true;
            int ok = 0, total = 0;
            PatchPhoton(ref ok, ref total, typeof(PhotonNetwork), "ExecuteRpc", typeof(RpcSender), "RpcPrefix", "RpcFinalizer",
                        "power, panel and synced-switch requests are not refused by the owner");
            PatchPhoton(ref ok, ref total, typeof(LoadBalancingClient), "OnEvent", typeof(InteractionGatePatches),
                        "DispatchPrefix", "DispatchFinalizer", "socket requests are not refused by the owner");
            PatchPhoton(ref ok, ref total, typeof(PhotonNetwork), "OnEvent", typeof(InteractionGatePatches),
                        "OwnershipEventPrefix", null, "ownership takeovers are not refused");
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

        /// <summary>A client query for an interactable, classified by the shared classifier.</summary>
        private static GateQuery ClientQuery(Component interactable)
        {
            Component resolved = InteractionClassifier.Resolve(interactable);
            return ClientQuery(resolved, InteractionClassifier.Classify(resolved, LocalCarrying()));
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

        /// <summary>A refused interactable is not a valid hit: no highlight, no prompt, and a
        /// warning when the player is within the game's default reach.</summary>
        private static void ValidHitPostfix(ref bool __result, ref AbstractInteractable interactable, float distance)
        {
            if (!__result || interactable == null || !InteractionGate.AnyRegistered) return;
            try
            {
                if (!InteractionGate.RefuseOnClient(ClientQuery(interactable), distance < WarnDistance)) return;
                __result = false;
                interactable = null;
            }
            catch (Exception e) { InteractionClassifier.LogHookFailure("RaycastHandler.IsValidInteractableHit", e); }
        }

        /// <summary>The raycast's final pick, after an item in a socket became the socket.</summary>
        private static void FindInteractablePostfix(ref AbstractInteractable interactable)
        {
            if (interactable == null || !InteractionGate.AnyRegistered) return;
            try
            {
                if (InteractionGate.RefuseOnClient(ClientQuery(interactable), false)) interactable = null;
            }
            catch (Exception e) { InteractionClassifier.LogHookFailure("RaycastHandler.FindInteractable", e); }
        }

        /// <summary>
        /// Every station the local player starts raises one event on their character. A clone
        /// tube and the periscope raise nothing else, so they are judged here, along with any
        /// station a mod adds. The others change state before or after raising it, and are judged
        /// at their own entry instead, before anything changes: a seat (and the helm or turret it
        /// starts) at <see cref="TrySitPrefix"/>, a terminal at <see cref="TerminalOpenPrefix"/>
        /// and <see cref="TerminalScreenPrefix"/>, a minigame at <see cref="MinigamePrefix"/>.
        /// Refusing those here would leave a seat taken with the player standing, or the view
        /// switched to a terminal the player cannot leave.
        /// </summary>
        private static bool ActivateAbilitiesPrefix(CustomCharacterLocomotion __instance, IAbilityActivator activator,
                                                    bool activate, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!activate || !InteractionGate.AnyRegistered) return true;
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

        /// <summary>A minigame the local player starts (a repair, a defect, a System Enhancement
        /// panel), before its skill modifier is applied.</summary>
        private static bool MinigamePrefix(CrypticCore __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!InteractionGate.AnyRegistered || __instance == null) return true;
            try
            {
                return !InteractionGate.RefuseOnClient(
                    ClientQuery(__instance, InteractionClassifier.Classify(__instance, false)), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("CrypticCore.Activate", e);
                return true;
            }
        }

        /// <summary>Backstop for the carry interaction: picking up, putting into a socket and
        /// taking out of one, on whatever the raycast offered.</summary>
        private static bool StartInteractionPrefix(AbstractInteractable interactable, LocalPlayer ___player,
                                                   bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (interactable == null || !InteractionGate.AnyRegistered) return true;
            try
            {
                if (___player == null || ___player != LocalPlayer.Instance) return true;
                return !InteractionGate.RefuseOnClient(ClientQuery(interactable), true);
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("CarryableInteract.StartInteraction", e);
                return true;
            }
        }

        /// <summary>The module upgrade tool aims by itself, without the raycast: a refused
        /// module is an invalid target, which stops the tool.</summary>
        private static void UpgradeTargetPostfix(InteractiveCarryableModuleUpgrader __instance, CellModule module,
                                                 ref bool __result)
        {
            if (__result || module == null || !InteractionGate.AnyRegistered) return;
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
        /// Every client: a network object set to be taken over at will changes owner on each
        /// client by itself, with no owner asked, when a player takes it (209) or is handed it
        /// (210). Every client judges the new owner and ignores a refused change, so the object
        /// stays with its owner everywhere except, at most, on the taker's own game. Judged: an
        /// item in a socket (taking it out), a loose item (a pick-up), and a part of a player
        /// ship. Not judged: an item a player carries, the host as the new owner, and anything
        /// else.
        /// </summary>
        private static bool OwnershipEventPrefix(EventData photonEvent, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (photonEvent == null || !InteractionGate.AnyRegistered) return true;
            byte code = photonEvent.Code;
            if (code != OwnershipRequestEvent && code != OwnershipTransferEvent) return true;
            try
            {
                int[] data = photonEvent.CustomData as int[];
                if (data == null || data.Length < 2) return true;
                int actor = code == OwnershipRequestEvent ? photonEvent.Sender : data[1];
                Photon.Realtime.Player player = PlayerOf(actor);
                if (player == null || player.IsMasterClient) return true;
                PhotonView view = PhotonView.Find(data[0]);
                if (view == null || view.OwnershipTransfer != OwnershipOption.Takeover) return true;

                Component part;
                InteractionKind kind;
                if (!OwnershipTarget(view, out part, out kind)) return true;
                return !InteractionGate.RefuseOnOwner(new GateQuery(player, part, kind, GateSide.Owner));
            }
            catch (Exception e)
            {
                InteractionClassifier.LogHookFailure("PhotonNetwork.OnEvent ownership", e);
                return true;
            }
        }

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
