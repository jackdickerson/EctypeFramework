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
        private const string SeatGateId = "ectype.dev.refuse-seats";

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
            const string first = "ectype.dev.selftest.first";
            const string second = "ectype.dev.selftest.second";
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
