using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Client.Ship;
using CG.Client.Ship.Interactions;
using CG.Client.Ship.Modules.Mediators;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Repair;
using Gameplay.CompositeWeapons;
using Gameplay.Defects;
using Gameplay.Enhancements;
using Gameplay.Power;
using Gameplay.Terminals;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// The interaction gate's shared classifier: what kind of thing a component is, worked out
    /// once per object and cached by instance id. The client side and the owner side reach every
    /// kind through this one path, so both read the same kind for the same part.
    ///
    /// Some controls are not under the object they control (a door's clickers, an airlock
    /// panel's lever and safety switch, a System Enhancement panel's switch and minigame, a
    /// seat's clicker, a module terminal's clicker, a repair's or a defect's clicker), so the
    /// objects that own them register them as they start, through postfixes patched here.
    /// Everything else is read from the component's type or the types above it.
    ///
    /// A switch is judged by what it switches: a power switch when a module mediator above it
    /// names it, else the kind of the component that holds the value it controls (the same
    /// component the owner side judges when the switch's request arrives). A switch with no
    /// module above it (a breaker, a line switch) is a power switch; one met while its module is
    /// still being assembled keeps that answer and is read again after
    /// <see cref="RetrySeconds"/>, at most <see cref="MaxRetries"/> times.
    /// </summary>
    internal static class InteractionClassifier
    {
        private const float RetrySeconds = 2f;
        private const int MaxRetries = 3;
        private const int CacheLimit = 8192;
        private const float PruneSeconds = 30f;

        private struct Registration
        {
            internal Component Control;
            internal InteractionKind Kind;
        }

        private struct Pending
        {
            internal InteractionKind Kind;
            internal float RetryAt;
            internal int Tries;
        }

        /// <summary>Controls registered by their owners as they start. Never cleared wholesale:
        /// an owner registers once, so a dropped live entry would not come back. Entries whose
        /// control is destroyed are pruned on a slow timer.</summary>
        private static readonly Dictionary<int, Registration> Registered = new Dictionary<int, Registration>();
        private static readonly Dictionary<int, InteractionKind> Cache = new Dictionary<int, InteractionKind>();
        private static readonly Dictionary<int, Pending> Unsettled = new Dictionary<int, Pending>();
        private static readonly Dictionary<Type, PropertyInfo> PowerSwitchProperty = new Dictionary<Type, PropertyInfo>();
        private static readonly List<TakeoverChair> Chairs = new List<TakeoverChair>();
        private static readonly List<int> Dead = new List<int>();
        private static float _nextPrune;

        private static AccessTools.FieldRef<DoorActor, List<ClickerInteractable>> _doorClickers;
        private static AccessTools.FieldRef<AirlockButtonPanel, ClickerInteractable> _pressurizeLever;
        private static AccessTools.FieldRef<AirlockButtonPanel, SwitchClicker> _safetySwitch;
        private static AccessTools.FieldRef<EnhancementCrypticActivator, CrypticCore> _panelCryptic;
        private static AccessTools.FieldRef<EnhancementCrypticActivator, ClickerInteractable> _panelCrypticClicker;
        private static AccessTools.FieldRef<EnhancementSwitchActivator, SwitchClicker> _panelSwitch;
        private static AccessTools.FieldRef<TerminalModuleMediator, ClickerInteractable> _terminalClicker;
        private static AccessTools.FieldRef<RepairObject, ClickerInteractable> _repairClicker;

        /// <summary>An item in a socket becomes the socket's interactable, as the game's own
        /// raycast does; everything else is returned as it is.</summary>
        internal static Component Resolve(Component c)
        {
            Grabbable grabbable = c as Grabbable;
            if (grabbable == null) return c;
            try
            {
                CarryableObject item = grabbable.Carryable;
                CarryablesSocket socket = item != null ? item.Carrier as CarryablesSocket : null;
                if (socket == null) return c;
                SocketInteractable si = socket.GetComponentInChildren<SocketInteractable>();
                return si != null ? (Component)si : socket;
            }
            catch (Exception) { return c; }
        }

        /// <summary>The kind of a resolved component. <paramref name="actorCarrying"/> decides
        /// between putting into and taking out of a socket.</summary>
        internal static InteractionKind Classify(Component c, bool actorCarrying)
        {
            if (c == null) return InteractionKind.Other;
            if (c is SocketInteractable || c is CarryablesSocket)
                return actorCarrying ? InteractionKind.SocketInsert : InteractionKind.SocketRelease;
            if (c is Grabbable || c is CarryableObject) return InteractionKind.CarryablePickup;

            int id = c.GetInstanceID();
            Registration reg;
            if (Registered.TryGetValue(id, out reg)) return reg.Kind;
            InteractionKind kind;
            if (Cache.TryGetValue(id, out kind)) return kind;
            Pending pending;
            bool wasPending = Unsettled.TryGetValue(id, out pending);
            if (wasPending && Time.unscaledTime < pending.RetryAt) return pending.Kind;

            bool settled;
            kind = Compute(c, out settled);
            int tries = wasPending ? pending.Tries + 1 : 1;
            if (settled || tries >= MaxRetries)
            {
                if (Cache.Count > CacheLimit) Cache.Clear();
                Cache[id] = kind;
                Unsettled.Remove(id);
            }
            else
            {
                if (Unsettled.Count > CacheLimit) Unsettled.Clear();
                Unsettled[id] = new Pending { Kind = kind, RetryAt = Time.unscaledTime + RetrySeconds, Tries = tries };
            }
            return kind;
        }

        /// <summary>The kind a seat gives: a turret's seat, the helm's, or any other.</summary>
        internal static InteractionKind ChairKind(TakeoverChair chair)
        {
            if (chair == null) return InteractionKind.Seat;
            if (chair is TurretChair || chair.Module is CompositeWeaponModule) return InteractionKind.Turret;
            if (chair.Module is Helm) return InteractionKind.Helm;
            return InteractionKind.Seat;
        }

        /// <summary>The seat whose connected network objects include this one, or null. A seat
        /// hands those objects to whoever sits in it.</summary>
        internal static TakeoverChair ChairHolding(PhotonView view)
        {
            if (view == null) return null;
            for (int i = 0; i < Chairs.Count; i++)
            {
                TakeoverChair chair = Chairs[i];
                if (chair != null && chair.ConnectedPhotonViews != null && chair.ConnectedPhotonViews.Contains(view))
                    return chair;
            }
            return null;
        }

        /// <summary>The kind read from a component's own type, or Other.</summary>
        private static InteractionKind ByType(Component c)
        {
            TakeoverChair chair = c as TakeoverChair;
            if (chair != null) return ChairKind(chair);
            if (c is Helm) return InteractionKind.Helm;
            if (c is CompositeWeaponModule) return InteractionKind.Turret;
            if (c is Periscope) return InteractionKind.Periscope;
            if (c is CloneTube) return InteractionKind.CloneTube;
            if (c is TerminalModule || c is TerminalScreen) return InteractionKind.Terminal;
            if (c is Enhancement) return InteractionKind.EnhancementPanel;
            if (c is RepairObject || c is Defect) return InteractionKind.Minigame;
            if (c is CrypticCore)
                return c.GetComponentInParent<Enhancement>(true) != null
                    ? InteractionKind.EnhancementPanel : InteractionKind.Minigame;
            if (c is AbstractDoor || c is DoorController) return InteractionKind.Door;
            if (c is Airlock) return InteractionKind.AirlockControl;
            if (c is PowerDrain || c is PowerDistributor || c is PowerBreaker) return InteractionKind.PowerSwitch;
            return InteractionKind.Other;
        }

        private static InteractionKind Compute(Component c, out bool settled)
        {
            settled = true;
            // The component itself. Only a seat's own component and its registered clicker count
            // as the seat: a seat can sit on a module's root, above the module's switches.
            InteractionKind kind = ByType(c);
            if (kind != InteractionKind.Other) return kind;

            SwitchClicker sw = c as SwitchClicker;
            if (sw != null)
            {
                // Checked before the parent lookups below, which would otherwise claim a station
                // module's own power switch.
                IShipModuleMediator[] mediators = sw.GetComponentsInParent<IShipModuleMediator>(true);
                foreach (IShipModuleMediator m in mediators)
                    if (ReferenceEquals(PowerSwitchOf(m), sw)) return InteractionKind.PowerSwitch;
                Component holder = HolderOf(sw);
                if (holder != null && holder != sw)
                {
                    kind = ByType(holder);
                    if (kind != InteractionKind.Other) return kind;
                }
                if (mediators.Length == 0)
                {
                    // Outside any module: a breaker or a line switch. A module still being
                    // assembled has no mediator yet either, so the answer is read again later.
                    settled = false;
                    return InteractionKind.PowerSwitch;
                }
            }

            // Station objects whose whole subtree is the station.
            if (c.GetComponentInParent<Enhancement>(true) != null) return InteractionKind.EnhancementPanel;
            if (c.GetComponentInParent<Periscope>(true) != null) return InteractionKind.Periscope;
            if (c.GetComponentInParent<CloneTube>(true) != null) return InteractionKind.CloneTube;
            if (c.GetComponentInParent<TerminalScreen>(true) != null) return InteractionKind.Terminal;
            if (c.GetComponentInParent<CrypticCore>(true) != null) return InteractionKind.Minigame;
            return InteractionKind.Other;
        }

        /// <summary>The component holding the synced value a switch controls, or null.</summary>
        private static Component HolderOf(SwitchClicker sw)
        {
            try
            {
                MemberReference<ChangeResponsiveBool> value = sw.ControlledValue;
                return value != null ? value.Component : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A module mediator's power switch: each mediator type declares one through its
        /// generic base, read by reflection once per type.</summary>
        private static Component PowerSwitchOf(IShipModuleMediator mediator)
        {
            if (mediator == null) return null;
            Type t = mediator.GetType();
            PropertyInfo p;
            if (!PowerSwitchProperty.TryGetValue(t, out p))
            {
                p = t.GetProperty("PowerSwitch", BindingFlags.Instance | BindingFlags.Public);
                if (p != null && !typeof(Component).IsAssignableFrom(p.PropertyType)) p = null;
                PowerSwitchProperty[t] = p;
            }
            try { return p != null ? p.GetValue(mediator, null) as Component : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Records a control under its owner's kind. Idempotent.</summary>
        internal static void RegisterControl(Component control, InteractionKind kind)
        {
            if (control == null) return;
            Registered[control.GetInstanceID()] = new Registration { Control = control, Kind = kind };
        }

        /// <summary>Drops a control registration and its cached kind (dev self-test).</summary>
        internal static void ForgetControl(Component control)
        {
            if (control == null) return;
            int id = control.GetInstanceID();
            Registered.Remove(id);
            Cache.Remove(id);
            Unsettled.Remove(id);
        }

        /// <summary>Driver: every 30 s, drops registrations and seats whose object is gone.</summary>
        internal static void PollPrune()
        {
            if (Time.unscaledTime < _nextPrune) return;
            _nextPrune = Time.unscaledTime + PruneSeconds;
            Dead.Clear();
            foreach (KeyValuePair<int, Registration> e in Registered)
                if (e.Value.Control == null) Dead.Add(e.Key);
            for (int i = 0; i < Dead.Count; i++) Registered.Remove(Dead[i]);
            Chairs.RemoveAll(c => c == null);
        }

        // ---- registration patches ------------------------------------------------------

        internal static void ApplyPatches(Harmony harmony)
        {
            _doorClickers = FieldRef<DoorActor, List<ClickerInteractable>>("clickers");
            _pressurizeLever = FieldRef<AirlockButtonPanel, ClickerInteractable>("pressurizationSwitch");
            _safetySwitch = FieldRef<AirlockButtonPanel, SwitchClicker>("safetySwitch");
            _panelCryptic = FieldRef<EnhancementCrypticActivator, CrypticCore>("cryptic");
            _panelCrypticClicker = FieldRef<EnhancementCrypticActivator, ClickerInteractable>("crypticClicker");
            _panelSwitch = FieldRef<EnhancementSwitchActivator, SwitchClicker>("switchClicker");
            _terminalClicker = FieldRef<TerminalModuleMediator, ClickerInteractable>("clicker");
            _repairClicker = FieldRef<RepairObject, ClickerInteractable>("clicker");

            Postfix(harmony, typeof(DoorActor), "Start", "DoorActorStart", _doorClickers != null,
                    "door clickers classify as Other");
            Postfix(harmony, typeof(AirlockButtonPanel), "Initialize", "AirlockPanelInitialize",
                    _pressurizeLever != null || _safetySwitch != null,
                    "airlock levers and safety switches classify as Other");
            Postfix(harmony, typeof(EnhancementCrypticActivator), "Awake", "PanelCrypticAwake",
                    _panelCryptic != null || _panelCrypticClicker != null,
                    "a panel's minigame outside the panel's hierarchy classifies as Minigame");
            Postfix(harmony, typeof(EnhancementSwitchActivator), "Awake", "PanelSwitchAwake",
                    _panelSwitch != null, "a panel's switch outside the panel's hierarchy classifies by what it switches");
            Postfix(harmony, typeof(TakeoverChair), "Awake", "ChairAwake", true,
                    "a seat's clicker shows its prompt and the seat is refused on use");
            Postfix(harmony, typeof(TerminalModuleMediator), "Start", "TerminalStart", _terminalClicker != null,
                    "a module terminal's clicker shows its prompt and the terminal is refused on use");
            Postfix(harmony, typeof(RepairObject), "Initialize", "RepairInitialize", _repairClicker != null,
                    "a repair's clicker shows its prompt and the repair is refused on use");
            Postfix(harmony, typeof(Defect), "Awake", "DefectAwake", true,
                    "a defect's clicker shows its prompt and the repair is refused on use");
        }

        private static AccessTools.FieldRef<T, F> FieldRef<T, F>(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(T), name);
            if (f == null || !typeof(F).IsAssignableFrom(f.FieldType))
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: " + typeof(T).Name + "." + name +
                                      " not found (game update?).");
                return null;
            }
            return AccessTools.FieldRefAccess<T, F>(f);
        }

        private static void Postfix(Harmony harmony, Type type, string method, string hook, bool usable,
                                    string consequence)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method, Type.EmptyTypes) ??
                                AccessTools.DeclaredMethod(type, method);
            if (target == null || !usable)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: " + type.Name + "." + method +
                                      " not usable (game update?); " + consequence + ".");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(InteractionClassifier), hook));
        }

        private static void DoorActorStart(DoorActor __instance)
        {
            try
            {
                List<ClickerInteractable> clickers = _doorClickers(__instance);
                if (clickers == null) return;
                foreach (ClickerInteractable c in clickers) RegisterControl(c, InteractionKind.Door);
            }
            catch (Exception e) { LogHookFailure("DoorActor.Start", e); }
        }

        private static void AirlockPanelInitialize(AirlockButtonPanel __instance)
        {
            try
            {
                if (_pressurizeLever != null) RegisterControl(_pressurizeLever(__instance), InteractionKind.AirlockControl);
                if (_safetySwitch != null) RegisterControl(_safetySwitch(__instance), InteractionKind.AirlockControl);
            }
            catch (Exception e) { LogHookFailure("AirlockButtonPanel.Initialize", e); }
        }

        private static void PanelCrypticAwake(EnhancementCrypticActivator __instance)
        {
            try
            {
                if (_panelCryptic != null) RegisterControl(_panelCryptic(__instance), InteractionKind.EnhancementPanel);
                if (_panelCrypticClicker != null)
                    RegisterControl(_panelCrypticClicker(__instance), InteractionKind.EnhancementPanel);
            }
            catch (Exception e) { LogHookFailure("EnhancementCrypticActivator.Awake", e); }
        }

        private static void PanelSwitchAwake(EnhancementSwitchActivator __instance)
        {
            try
            {
                if (_panelSwitch != null) RegisterControl(_panelSwitch(__instance), InteractionKind.EnhancementPanel);
            }
            catch (Exception e) { LogHookFailure("EnhancementSwitchActivator.Awake", e); }
        }

        private static void TerminalStart(TerminalModuleMediator __instance)
        {
            try
            {
                if (_terminalClicker != null) RegisterControl(_terminalClicker(__instance), InteractionKind.Terminal);
            }
            catch (Exception e) { LogHookFailure("TerminalModuleMediator.Start", e); }
        }

        private static void RepairInitialize(RepairObject __instance)
        {
            try
            {
                if (_repairClicker != null) RegisterControl(_repairClicker(__instance), InteractionKind.Minigame);
            }
            catch (Exception e) { LogHookFailure("RepairObject.Initialize", e); }
        }

        private static void DefectAwake(Defect __instance)
        {
            try
            {
                if (__instance != null) RegisterControl(__instance.clicker, InteractionKind.Minigame);
            }
            catch (Exception e) { LogHookFailure("Defect.Awake", e); }
        }

        private static void ChairAwake(TakeoverChair __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!Chairs.Contains(__instance)) Chairs.Add(__instance);
                if (__instance.Clicker != null) RegisterControl(__instance.Clicker, ChairKind(__instance));
            }
            catch (Exception e) { LogHookFailure("TakeoverChair.Awake", e); }
        }

        private static readonly HashSet<string> LoggedHooks = new HashSet<string>();

        internal static void LogHookFailure(string hook, Exception e)
        {
            if (LoggedHooks.Add(hook))
                Plugin.Log.LogError("Ectype: interaction gate: " + hook + " hook failed (logged once): " + e);
        }
    }
}
