using System;
using System.Collections.Generic;

using BepInEx.Configuration;
using CG.Ship.Modules;
using Gameplay.Terminals;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev tools build only: the interaction gate's self-test, run once at load, and the
    /// [Dev] RefuseAllSeats switch for checking the gate in game.
    ///
    /// The self-test drives the warning throttle with its own clock, classifies game components
    /// added to an inactive object (so none of their Awake or OnEnable runs), and evaluates
    /// queries against gates it registers and removes again. It makes no Photon call: a query's
    /// player is left null, because touching PhotonNetwork during plugin load sets Photon up
    /// before the game does.
    /// </summary>
    internal static class InteractionGateDev
    {
        private const string SeatGateId = EctypePluginInfo.PLUGIN_GUID + "/dev.refuse-seats";

        internal static ConfigEntry<bool> RefuseAllSeats;

        internal static void Bind(ConfigFile cfg)
        {
            RefuseAllSeats = cfg.Bind("Dev", "RefuseAllSeats", false,
                "Register an interaction gate that refuses every seat (any seat, the helm, turrets) " +
                "to everyone, for checking the gate in game. Applies at once. On the acting client " +
                "the seat shows no prompt and a warning; with the switch on only on the host, a " +
                "client's seat request is dropped by the host.");
            RefuseAllSeats.SettingChanged += (s, e) => ApplySeatGate();
            ApplySeatGate();
        }

        private static void ApplySeatGate()
        {
            InteractionGate.Unregister(SeatGateId);
            if (!RefuseAllSeats.Value) return;
            InteractionGate.Register(SeatGateId, q =>
                q.Kind == InteractionKind.Seat || q.Kind == InteractionKind.Helm || q.Kind == InteractionKind.Turret
                    ? Refusal.Because("[Dev] RefuseAllSeats", "DEV: EVERY SEAT IS REFUSED ([Dev] RefuseAllSeats).")
                    : Refusal.Allowed);
        }

        internal static void SelfTest()
        {
            List<string> failures = new List<string>();
            List<string> skipped = new List<string>();
            Throttle(failures);
            Evaluation(failures);
            Classifier(failures, skipped);

            string tail = skipped.Count > 0 ? "; not run: " + string.Join(", ", skipped.ToArray()) : "";
            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: interaction gate passed (throttle, evaluation order, " +
                                   "classifier)" + tail + ".");
            else
                Plugin.Log.LogError("Ectype: dev self-test: interaction gate FAILED: " +
                                    string.Join("; ", failures.ToArray()) + tail);
        }

        private static void Throttle(List<string> failures)
        {
            GateWarnings.Reset();
            try
            {
                Expect(failures, "first text shows", GateWarnings.Admit("A", 100f), true);
                Expect(failures, "another text within 3 s is held", GateWarnings.Admit("B", 102f), false);
                Expect(failures, "another text after 3 s shows", GateWarnings.Admit("B", 103.1f), true);
                Expect(failures, "same text within 20 s is held", GateWarnings.Admit("A", 110f), false);
                Expect(failures, "a third text after the gap shows", GateWarnings.Admit("C", 110.5f), true);
                Expect(failures, "same text after 20 s shows", GateWarnings.Admit("A", 120.5f), true);
                Expect(failures, "empty text never shows", GateWarnings.Admit("", 200f), false);
            }
            finally { GateWarnings.Reset(); }
        }

        private static void Evaluation(List<string> failures)
        {
            const string first = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest.first";
            const string second = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest.second";
            try
            {
                InteractionGate.Register(first, q => q.Kind == InteractionKind.Helm
                    ? Refusal.Because("self-test helm", "FIRST") : Refusal.Allowed);
                InteractionGate.Register(second, q => q.Kind == InteractionKind.Helm || q.Kind == InteractionKind.Door
                    ? Refusal.Because("self-test helm or door", "SECOND") : null);

                Refusal helm = InteractionGate.Evaluate(new GateQuery(null, null, InteractionKind.Helm, GateSide.Client));
                Expect(failures, "a refused helm is refused", !helm.IsAllowed, true);
                Expect(failures, "the first registered refusal's text wins", helm.Text == "FIRST", true);
                Refusal door = InteractionGate.Evaluate(new GateQuery(null, null, InteractionKind.Door, GateSide.Owner));
                Expect(failures, "a later gate still refuses what an earlier one allows", door.Text == "SECOND", true);
                Refusal seat = InteractionGate.Evaluate(new GateQuery(null, null, InteractionKind.Seat, GateSide.Client));
                Expect(failures, "a null answer allows", seat.IsAllowed, true);
                Expect(failures, "a silent refusal has no text", Refusal.Silently("x").Silent, true);
                Expect(failures, "Allowed is allowed", Refusal.Allowed.IsAllowed, true);
            }
            catch (Exception e) { failures.Add("evaluation threw: " + e.Message); }
            finally
            {
                Expect(failures, "unregister removes a gate", InteractionGate.Unregister(first), true);
                Expect(failures, "unregister of an unknown id is false", InteractionGate.Unregister(first), false);
                InteractionGate.Unregister(second);
            }
        }

        private static void Classifier(List<string> failures, List<string> skipped)
        {
            GameObject root = new GameObject("Ectype.GateSelfTest");
            root.SetActive(false);
            root.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                Expect(failures, "a null component is Other",
                       InteractionClassifier.Classify(null, false) == InteractionKind.Other, true);
                Expect(failures, "a bare transform is Other",
                       InteractionClassifier.Classify(root.transform, false) == InteractionKind.Other, true);

                Probe(failures, skipped, root, "seat", InteractionKind.Seat, go => go.AddComponent<TakeoverChair>());
                Probe(failures, skipped, root, "turret seat", InteractionKind.Turret, go => go.AddComponent<TurretChair>());
                Probe(failures, skipped, root, "helm seat", InteractionKind.Helm, go =>
                {
                    TakeoverChair chair = go.AddComponent<TakeoverChair>();
                    GameObject helm = Child(root, "helm");
                    chair.Module = helm.AddComponent<Helm>();
                    return chair;
                });
                Probe(failures, skipped, root, "periscope", InteractionKind.Periscope, go => go.AddComponent<Periscope>());
                Probe(failures, skipped, root, "control under a periscope", InteractionKind.Periscope, go =>
                {
                    go.AddComponent<Periscope>();
                    return Child(go, "button").transform;
                });
                Probe(failures, skipped, root, "clone tube", InteractionKind.CloneTube, go => go.AddComponent<CloneTube>());
                Probe(failures, skipped, root, "terminal screen", InteractionKind.Terminal, go => go.AddComponent<TerminalScreen>());
                Probe(failures, skipped, root, "registered door control", InteractionKind.Door, go =>
                {
                    InteractionClassifier.RegisterControl(go.transform, InteractionKind.Door);
                    return go.transform;
                });
                Probe(failures, skipped, root, "repair", InteractionKind.Minigame, go => go.AddComponent<CG.Ship.Repair.RepairObject>());
                Probe(failures, skipped, root, "defect", InteractionKind.Minigame, go => go.AddComponent<Gameplay.Defects.Defect>());
                SwitchConverges(failures, skipped, root);
                RpcContextCases(failures);
                PowerKinds(failures, skipped, root);
                SameFromEveryEntry(failures, skipped, root);
            }
            catch (Exception e) { failures.Add("classifier threw: " + e.Message); }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void Probe(List<string> failures, List<string> skipped, GameObject root, string name,
                                  InteractionKind expected, Func<GameObject, Component> build)
        {
            Component c;
            try { c = build(Child(root, name)); }
            catch (Exception e)
            {
                skipped.Add(name + " (" + e.GetType().Name + ")");
                return;
            }
            InteractionKind got = InteractionClassifier.Classify(c, false);
            if (got != expected) failures.Add(name + " classified " + got + ", expected " + expected);
            InteractionClassifier.ForgetControl(c);
        }

        /// <summary>A switch outside any module reads as a power switch, and reads the same again
        /// while its answer is still provisional (the client and the owner side must agree).</summary>
        private static void SwitchConverges(List<string> failures, List<string> skipped, GameObject root)
        {
            Component sw;
            try { sw = Child(root, "loose switch").AddComponent<CG.Client.Ship.Interactions.SwitchClicker>(); }
            catch (Exception e)
            {
                skipped.Add("loose switch (" + e.GetType().Name + ")");
                return;
            }
            InteractionKind first = InteractionClassifier.Classify(sw, false);
            InteractionKind again = InteractionClassifier.Classify(sw, false);
            Expect(failures, "a provisional switch answer is not cached for good", InteractionClassifier.IsCached(sw), false);
            if (first != InteractionKind.OtherPowerSwitch || again != first)
                failures.Add("a switch outside any module classified " + first + " then " + again +
                             ", expected OtherPowerSwitch both times");
            InteractionClassifier.ForgetControl(sw);
        }

        /// <summary>
        /// The power and airlock kinds: each power component names its own kind, a switch is
        /// classified as the power component holding its value or as the module mediator that
        /// names it (the central computer's being the ship's power), and the coarse helpers cover
        /// exactly the split kinds.
        /// </summary>
        private static void PowerKinds(List<string> failures, List<string> skipped, GameObject root)
        {
            ExpectType(failures, typeof(Gameplay.Power.PowerSystem), InteractionKind.ShipPowerSwitch);
            ExpectType(failures, typeof(Gameplay.Power.ProtectedPowerSystem), InteractionKind.ShipPowerSwitch);
            ExpectType(failures, typeof(Gameplay.Power.PowerPropagator), InteractionKind.PowerLineSwitch);
            ExpectType(failures, typeof(Gameplay.Power.PowerDrain), InteractionKind.ModulePowerSwitch);
            ExpectType(failures, typeof(Gameplay.Power.PowerBreaker), InteractionKind.Breaker);
            ExpectType(failures, typeof(CG.Client.Ship.Airlock), InteractionKind.AirlockPressurize);

            HeldSwitch<Gameplay.Power.PowerDrain>(failures, skipped, root, InteractionKind.ModulePowerSwitch);
            HeldSwitch<Gameplay.Power.PowerSystem>(failures, skipped, root, InteractionKind.ShipPowerSwitch);
            HeldSwitch<Gameplay.Power.PowerPropagator>(failures, skipped, root, InteractionKind.PowerLineSwitch);
            HeldSwitch<Gameplay.Power.PowerBreaker>(failures, skipped, root, InteractionKind.Breaker);

            Probe(failures, skipped, root, "switch under a breaker", InteractionKind.Breaker, go =>
            {
                go.AddComponent<Gameplay.Power.PowerBreaker>();
                return Child(go, "lever").AddComponent<CG.Client.Ship.Interactions.SwitchClicker>();
            });
            Probe(failures, skipped, root, "a module's power switch", InteractionKind.ModulePowerSwitch, go =>
                MediatorSwitch(go, go.AddComponent<CG.Client.Ship.Modules.Mediators.LifeSupportModuleMediator>()));
            Probe(failures, skipped, root, "a module-named switch holding the power system", InteractionKind.ShipPowerSwitch, go =>
            {
                var sw = (CG.Client.Ship.Interactions.SwitchClicker)MediatorSwitch(go,
                    go.AddComponent<CG.Client.Ship.Modules.Mediators.LifeSupportModuleMediator>());
                sw.ControlledValue = new MemberReference<ChangeResponsiveBool>
                    { Component = go.AddComponent<Gameplay.Power.PowerSystem>(), Member = "IsOn" };
                return sw;
            });
            Probe(failures, skipped, root, "the central computer's power switch", InteractionKind.ShipPowerSwitch, go =>
                MediatorSwitch(go, go.AddComponent<CG.Client.Ship.Modules.Mediators.CentralShipComputerMediator>()));

            int power = 0, airlock = 0;
            foreach (InteractionKind k in Enum.GetValues(typeof(InteractionKind)))
            {
                if (k.IsPowerSwitch()) power++;
                if (InteractionKinds.IsAirlockControl(k)) airlock++;
            }
            Expect(failures, "IsPowerSwitch covers the five power kinds", power == 5, true);
            Expect(failures, "IsAirlockControl covers the two airlock kinds", airlock == 2, true);
            Expect(failures, "a breaker is a power switch", InteractionKind.Breaker.IsPowerSwitch(), true);
            Expect(failures, "a door is not an airlock control", InteractionKind.Door.IsAirlockControl(), false);
        }

        /// <summary>The sender test: a frame whose sender left but whose actor is known is absent; a
        /// server-sent message (actor 0), a live sender and another view are not. A finalizer whose
        /// prefix did not run leaves the current frame alone.</summary>
        internal static void RpcContextCases(List<string> failures)
        {
            RpcContext.Frame left = new RpcContext.Frame { Source = RpcSource.Rpc, Actor = 3, View = 7 };
            RpcContext.Frame server = new RpcContext.Frame { Source = RpcSource.Rpc, Actor = 0, View = 7 };
            Expect(failures, "an RPC from a player who left is from an absent player", RpcContext.IsAbsent(left, 7), true);
            Expect(failures, "an RPC with actor 0 is not from an absent player", RpcContext.IsAbsent(server, 7), false);
            Expect(failures, "an RPC on another view is not", RpcContext.IsAbsent(left, 8), false);
            Expect(failures, "an event is not", RpcContext.IsAbsent(new RpcContext.Frame
                { Source = RpcSource.Event, Actor = 3, View = 7 }, 7), false);

            RpcContext.Saved outer = RpcContext.Push(left);
            try
            {
                RpcContext.Restore(default(RpcContext.Saved));
                Expect(failures, "a finalizer whose prefix did not run keeps the frame",
                       RpcContext.CurrentFrame.Actor == 3, true);
                RpcContext.Saved inner = RpcContext.Push(server);
                RpcContext.Restore(inner);
                Expect(failures, "a finalizer restores the frame its prefix saved",
                       RpcContext.CurrentFrame.Actor == 3, true);
            }
            finally { RpcContext.Restore(outer); }
        }

        private static void ExpectType(List<string> failures, Type type, InteractionKind expected)
        {
            InteractionKind got = InteractionClassifier.KindOfType(type);
            if (got != expected) failures.Add(type.Name + " names " + got + ", expected " + expected);
        }

        /// <summary>A switch whose controlled value is held by a component of type T.</summary>
        private static void HeldSwitch<T>(List<string> failures, List<string> skipped, GameObject root,
                                          InteractionKind expected) where T : MonoBehaviour
        {
            Probe(failures, skipped, root, "switch holding a " + typeof(T).Name + " value", expected, go =>
            {
                T holder = go.AddComponent<T>();
                var sw = Child(go, "switch").AddComponent<CG.Client.Ship.Interactions.SwitchClicker>();
                sw.ControlledValue = new MemberReference<ChangeResponsiveBool> { Component = holder, Member = "IsOn" };
                return sw;
            });
        }

        /// <summary>A switch below a module mediator that names it as its power switch.</summary>
        private static Component MediatorSwitch(GameObject go, MonoBehaviour mediator)
        {
            var sw = Child(go, "power switch").AddComponent<CG.Client.Ship.Interactions.SwitchClicker>();
            HarmonyLib.Traverse.Create(mediator).Field("powerSwitch").SetValue(sw);
            return sw;
        }

        /// <summary>
        /// Every chokepoint presents one thing as one component with one kind: an item through
        /// its grab handle or itself, a socket through its interactable or itself, and a switch
        /// as the component holding its value (what the owner side judges).
        /// </summary>
        private static void SameFromEveryEntry(List<string> failures, List<string> skipped, GameObject root)
        {
            try
            {
                GameObject go = Child(root, "item");
                CG.Objects.CarryableObject item = go.AddComponent<CG.Objects.CarryableObject>();
                Component grab = go.AddComponent<CG.Client.Ship.Interactions.Grabbable>();
                Same(failures, "item", item, grab);
            }
            catch (Exception e) { skipped.Add("item entries (" + e.GetType().Name + ")"); }

            try
            {
                GameObject go = Child(root, "socket");
                Component socket = go.AddComponent<CG.Ship.Hull.CarryablesSocket>();
                Component si = Child(go, "socket interactable").AddComponent<CG.Client.Ship.Interactions.SocketInteractable>();
                Same(failures, "socket", socket, si);
            }
            catch (Exception e) { skipped.Add("socket entries (" + e.GetType().Name + ")"); }

            try
            {
                // A holder whose type names no kind, under a clone tube: both sides must run the
                // parent lookup from the holder, not from the switch.
                GameObject tube = Child(root, "tube");
                tube.AddComponent<CloneTube>();
                MonoBehaviour holder = Child(tube, "holder").AddComponent<CG.Client.Ship.Interactions.DoorActor>();
                var sw = Child(root, "held switch").AddComponent<CG.Client.Ship.Interactions.SwitchClicker>();
                sw.ControlledValue = new MemberReference<ChangeResponsiveBool> { Component = holder, Member = "x" };
                InteractionKind fromSwitch = InteractionClassifier.Classify(InteractionClassifier.Canonical(sw), false);
                InteractionKind fromHolder = InteractionClassifier.Classify(InteractionClassifier.Canonical(holder), false);
                if (fromSwitch != fromHolder || fromHolder != InteractionKind.CloneTube)
                    failures.Add("a switch classified " + fromSwitch + " and its value holder " + fromHolder +
                                 ", expected CloneTube both");
                InteractionClassifier.ForgetControl(sw);
                InteractionClassifier.ForgetControl(holder);
            }
            catch (Exception e) { skipped.Add("switch and holder (" + e.GetType().Name + ")"); }
        }

        private static void Same(List<string> failures, string name, Component canonical, Component other)
        {
            Component a = InteractionClassifier.Canonical(canonical);
            Component b = InteractionClassifier.Canonical(other);
            if (a != canonical || b != canonical)
                failures.Add(name + " reaches gates as " + (a != null ? a.GetType().Name : "null") + " and " +
                             (b != null ? b.GetType().Name : "null") + ", expected " + canonical.GetType().Name);
            InteractionKind ka = InteractionClassifier.Classify(a, false), kb = InteractionClassifier.Classify(b, false);
            if (ka != kb) failures.Add(name + " classified " + ka + " and " + kb);
        }

        private static GameObject Child(GameObject parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void Expect(List<string> failures, string what, bool got, bool want)
        {
            if (got != want) failures.Add(what + " (got " + got + ")");
        }
    }
}
