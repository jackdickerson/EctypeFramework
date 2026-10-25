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
    /// A switch is judged by what it switches: the full classification of the component that
    /// holds the value it controls (the same component, through the same computation, as the
    /// owner side when the switch's request arrives), else a module's power switch when a module
    /// mediator above it names it (the ship's power switch when that mediator is the central
    /// computer's). A switch with no module above it and no value holder is a breaker when it sits on
    /// or under one, else <see cref="InteractionKind.OtherPowerSwitch"/>; one met while its module
    /// is still being assembled gets that answer as provisional: it is never cached, and is read
    /// again once <see cref="RetrySeconds"/> have passed, until a module above the switch or a
    /// value holder settles it.
    ///
    /// Every query starts from <see cref="Canonical"/>, so an item or a socket reaches a gate as
    /// the same component from every chokepoint.
    /// </summary>
    internal static class InteractionClassifier
    {
        private const float RetrySeconds = 2f;
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

        /// <summary>
        /// The one component a gate sees for a thing, whichever chokepoint met it: an item is its
        /// <c>CarryableObject</c> (from its grab handle too), an item sitting in a socket is that
        /// <c>CarryablesSocket</c>, as the game's own raycast reads it, and a socket's interactable
        /// is its <c>CarryablesSocket</c>. Everything else is returned as it is. Every query the
        /// framework builds, on either side, starts from this.
        /// </summary>
        internal static Component Canonical(Component c)
        {
            if (c == null) return null;
            try
            {
                SocketInteractable si = c as SocketInteractable;
                if (si != null)
                {
                    CarryablesSocket own = si.SocketActor != null ? si.SocketActor.Socket : null;
                    if (own == null) own = si.GetComponentInParent<CarryablesSocket>(true);
                    return own != null ? own : c;
                }
                Grabbable grabbable = c as Grabbable;
                CarryableObject item = grabbable != null ? grabbable.Carryable : c as CarryableObject;
                if (item == null) return c;
                CarryablesSocket socket = item.Carrier as CarryablesSocket;
                return socket != null ? (Component)socket : item;
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
            if (settled)
            {
                if (Cache.Count > CacheLimit) Cache.Clear();
                Cache[id] = kind;
                Unsettled.Remove(id);
            }
            else
            {
                if (Unsettled.Count > CacheLimit) Unsettled.Clear();
                Unsettled[id] = new Pending { Kind = kind, RetryAt = Time.unscaledTime + RetrySeconds };
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
            if (c is CrypticCore)
                return c.GetComponentInParent<Enhancement>(true) != null
                    ? InteractionKind.EnhancementPanel : InteractionKind.Minigame;
            return KindOfType(c.GetType());
        }

        /// <summary>The kind a component type names by itself, seats and minigame cores aside
        /// (theirs depends on what is around them). A power component's kind is the switch that
        /// controls it: the power system is the ship's power, a propagator is a power line, a
        /// drain is a module's power.</summary>
        internal static InteractionKind KindOfType(Type t)
        {
            if (t == null) return InteractionKind.Other;
            if (typeof(Helm).IsAssignableFrom(t)) return InteractionKind.Helm;
            if (typeof(CompositeWeaponModule).IsAssignableFrom(t)) return InteractionKind.Turret;
            if (typeof(Periscope).IsAssignableFrom(t)) return InteractionKind.Periscope;
            if (typeof(CloneTube).IsAssignableFrom(t)) return InteractionKind.CloneTube;
            if (typeof(TerminalModule).IsAssignableFrom(t) || typeof(TerminalScreen).IsAssignableFrom(t))
                return InteractionKind.Terminal;
            if (typeof(Enhancement).IsAssignableFrom(t)) return InteractionKind.EnhancementPanel;
            if (typeof(RepairObject).IsAssignableFrom(t) || typeof(Defect).IsAssignableFrom(t))
                return InteractionKind.Minigame;
            if (typeof(AbstractDoor).IsAssignableFrom(t) || typeof(DoorController).IsAssignableFrom(t))
                return InteractionKind.Door;
            if (typeof(Airlock).IsAssignableFrom(t)) return InteractionKind.AirlockPressurize;
            if (typeof(PowerSystem).IsAssignableFrom(t)) return InteractionKind.ShipPowerSwitch;
            if (typeof(PowerPropagator).IsAssignableFrom(t)) return InteractionKind.PowerLineSwitch;
            if (typeof(PowerDrain).IsAssignableFrom(t)) return InteractionKind.ModulePowerSwitch;
            if (typeof(PowerBreaker).IsAssignableFrom(t)) return InteractionKind.Breaker;
            if (typeof(PowerDistributor).IsAssignableFrom(t)) return InteractionKind.OtherPowerSwitch;
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
                // module's own power switch. The central computer's power switch is the whole
                // ship's: its mediator hands it the power system's state.
                IShipModuleMediator[] mediators = sw.GetComponentsInParent<IShipModuleMediator>(true);
                // The component the owner judges when this switch's request arrives: the same
                // computation from the same component, so both sides read the same kind. A
                // mediator names the kind only where the switch holds nothing the classifier
                // can place.
                Component holder = HolderOf(sw);
                bool held = holder != null && holder != sw && !(holder is SwitchClicker);
                foreach (IShipModuleMediator m in mediators)
                {
                    if (!ReferenceEquals(PowerSwitchOf(m), sw)) continue;
                    if (held)
                    {
                        bool heldSettled;
                        InteractionKind heldKind = Compute(holder, out heldSettled);
                        if (heldKind != InteractionKind.Other)
                        {
                            settled = heldSettled;
                            return heldKind;
                        }
                    }
                    return IsCentralComputer(m) ? InteractionKind.ShipPowerSwitch : InteractionKind.ModulePowerSwitch;
                }
                if (held) return Compute(holder, out settled);
                if (mediators.Length == 0)
                {
                    // Outside any module, holding no value the classifier can see. A switch on or
                    // under a breaker is the breaker's. Otherwise the answer is provisional: a
                    // module still being assembled has no mediator yet, so it is read again later.
                    if (sw.GetComponentInParent<PowerBreaker>(true) != null) return InteractionKind.Breaker;
                    settled = false;
                    return InteractionKind.OtherPowerSwitch;
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

        private static bool IsCentralComputer(IShipModuleMediator m)
        {
            try { return m is CentralShipComputerMediator || m.Module is CentralShipComputerModule; }
            catch (Exception) { return false; }
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

        /// <summary>Whether a component's kind is cached for good (dev self-test): false while its
        /// answer is provisional.</summary>
        internal static bool IsCached(Component control)
        {
            return control != null && Cache.ContainsKey(control.GetInstanceID());
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
            // Each registration on its own: a failure costs that control's classification only.
            try
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
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: " + type.Name + "." + method +
                                      " not usable (game update?); " + consequence + ": " + e.Message);
            }
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
                if (_pressurizeLever != null) RegisterControl(_pressurizeLever(__instance), InteractionKind.AirlockPressurize);
                if (_safetySwitch != null) RegisterControl(_safetySwitch(__instance), InteractionKind.AirlockSafety);
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
