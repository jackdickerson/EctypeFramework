using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Client.Ship;
using CG.Client.Ship.Interactions;
using CG.Client.Ship.Modules.Mediators;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Modules;
using Gameplay.CompositeWeapons;
using Gameplay.Enhancements;
using Gameplay.Power;
using Gameplay.Terminals;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// The interaction gate's shared classifier: what kind of thing a component is, worked out
    /// once per object and cached by instance id.
    ///
    /// Some controls are not under the object they control (a door's clickers, an airlock
    /// panel's lever and safety switch, a System Enhancement panel's switch and minigame, a
    /// seat's clicker), so the objects that own them register them as they start, through
    /// postfixes patched here. Everything else is read from the component's type or the types
    /// above it. A switch is a power switch when a module mediator above it names it as its
    /// power switch; a switch seen while its module is still being put together is looked at
    /// again after <see cref="RetrySeconds"/>.
    /// </summary>
    internal static class InteractionClassifier
    {
        private const float RetrySeconds = 2f;
        private const int CacheLimit = 8192;

        /// <summary>Controls registered by their owners as they start.</summary>
        private static readonly Dictionary<int, InteractionKind> Registered = new Dictionary<int, InteractionKind>();
        private static readonly Dictionary<int, InteractionKind> Cache = new Dictionary<int, InteractionKind>();
        private static readonly Dictionary<int, float> RetryAt = new Dictionary<int, float>();
        private static readonly Dictionary<Type, PropertyInfo> PowerSwitchProperty = new Dictionary<Type, PropertyInfo>();

        private static AccessTools.FieldRef<DoorActor, List<ClickerInteractable>> _doorClickers;
        private static AccessTools.FieldRef<AirlockButtonPanel, ClickerInteractable> _pressurizeLever;
        private static AccessTools.FieldRef<AirlockButtonPanel, SwitchClicker> _safetySwitch;
        private static AccessTools.FieldRef<EnhancementCrypticActivator, CrypticCore> _panelCryptic;
        private static AccessTools.FieldRef<EnhancementCrypticActivator, ClickerInteractable> _panelCrypticClicker;
        private static AccessTools.FieldRef<EnhancementSwitchActivator, SwitchClicker> _panelSwitch;
        private static AccessTools.FieldRef<TerminalModuleMediator, ClickerInteractable> _terminalClicker;

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
            InteractionKind kind;
            if (Registered.TryGetValue(id, out kind)) return kind;
            if (Cache.TryGetValue(id, out kind)) return kind;
            float retry;
            if (RetryAt.TryGetValue(id, out retry) && Time.unscaledTime < retry) return InteractionKind.Other;

            bool settled;
            kind = Compute(c, out settled);
            if (Cache.Count > CacheLimit) Cache.Clear();
            if (settled)
            {
                Cache[id] = kind;
                RetryAt.Remove(id);
            }
            else
            {
                if (RetryAt.Count > CacheLimit) RetryAt.Clear();
                RetryAt[id] = Time.unscaledTime + RetrySeconds;
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

        private static InteractionKind Compute(Component c, out bool settled)
        {
            settled = true;
            // The component itself. Only a seat's own component and its registered clicker count
            // as the seat: a seat can sit on a module's root, above the module's switches.
            TakeoverChair chair = c as TakeoverChair;
            if (chair != null) return ChairKind(chair);
            if (c is Helm) return InteractionKind.Helm;
            if (c is CompositeWeaponModule) return InteractionKind.Turret;
            if (c is Periscope) return InteractionKind.Periscope;
            if (c is CloneTube) return InteractionKind.CloneTube;
            if (c is TerminalModule || c is TerminalScreen) return InteractionKind.Terminal;
            if (c is Enhancement) return InteractionKind.EnhancementPanel;
            if (c is CrypticCore)
                return c.GetComponentInParent<Enhancement>(true) != null
                    ? InteractionKind.EnhancementPanel : InteractionKind.Minigame;
            if (c is AbstractDoor || c is DoorController) return InteractionKind.Door;
            if (c is Airlock) return InteractionKind.AirlockControl;
            if (c is PowerDrain || c is PowerDistributor || c is PowerBreaker) return InteractionKind.PowerSwitch;

            // A module's power switch: a mediator above it names it. Checked before the parent
            // lookups below, which would otherwise claim a station module's own power switch.
            SwitchClicker sw = c as SwitchClicker;
            if (sw != null)
            {
                IShipModuleMediator[] mediators = sw.GetComponentsInParent<IShipModuleMediator>(true);
                // A module still being put together has no mediator yet.
                if (mediators.Length == 0) settled = false;
                foreach (IShipModuleMediator m in mediators)
                    if (ReferenceEquals(PowerSwitchOf(m), sw)) return InteractionKind.PowerSwitch;
            }

            // Station objects whose whole subtree is the station.
            if (c.GetComponentInParent<Enhancement>(true) != null) return InteractionKind.EnhancementPanel;
            if (c.GetComponentInParent<Periscope>(true) != null) return InteractionKind.Periscope;
            if (c.GetComponentInParent<CloneTube>(true) != null) return InteractionKind.CloneTube;
            if (c.GetComponentInParent<TerminalScreen>(true) != null) return InteractionKind.Terminal;
            if (c.GetComponentInParent<CrypticCore>(true) != null) return InteractionKind.Minigame;
            return InteractionKind.Other;
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
            if (Registered.Count > CacheLimit) DropDestroyed();
            Registered[control.GetInstanceID()] = kind;
        }

        /// <summary>Drops a control registration (dev self-test).</summary>
        internal static void ForgetControl(Component control)
        {
            if (control == null) return;
            int id = control.GetInstanceID();
            Registered.Remove(id);
            Cache.Remove(id);
            RetryAt.Remove(id);
        }

        private static void DropDestroyed()
        {
            // Instance ids of destroyed objects are not looked up again; the map is simply
            // restarted, and live controls re-register as their owners start.
            Registered.Clear();
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

            Postfix(harmony, typeof(DoorActor), "Start", "DoorActorStart", _doorClickers != null,
                    "door clickers classify as Other");
            Postfix(harmony, typeof(AirlockButtonPanel), "Initialize", "AirlockPanelInitialize",
                    _pressurizeLever != null || _safetySwitch != null,
                    "airlock levers and safety switches classify as Other");
            Postfix(harmony, typeof(EnhancementCrypticActivator), "Awake", "PanelCrypticAwake",
                    _panelCryptic != null || _panelCrypticClicker != null,
                    "a panel's minigame outside the panel's hierarchy classifies as Minigame");
            Postfix(harmony, typeof(EnhancementSwitchActivator), "Awake", "PanelSwitchAwake",
                    _panelSwitch != null, "a panel's switch outside the panel's hierarchy classifies as Other");
            Postfix(harmony, typeof(TakeoverChair), "Awake", "ChairAwake", true,
                    "a seat's clicker classifies as Other until the seat is used");
            Postfix(harmony, typeof(TerminalModuleMediator), "Start", "TerminalStart", _terminalClicker != null,
                    "a module terminal's clicker classifies as Other until the terminal opens");
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

        private static void ChairAwake(TakeoverChair __instance)
        {
            try
            {
                if (__instance != null && __instance.Clicker != null)
                    RegisterControl(__instance.Clicker, ChairKind(__instance));
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
