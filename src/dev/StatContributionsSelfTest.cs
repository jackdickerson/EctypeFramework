using System;
using System.Collections.Generic;
using System.Reflection;

using Client.Utils;
using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test for lasting stat contributions, offline on the driver's first frame,
    /// against targets with no game object behind them:
    /// - Validation: an id whose part before the '/' is not a loaded plugin's GUID, a missing
    ///   Amount and a second registration of a live id are refused.
    /// - Once per target: a target spawned twice carries one modifier per contribution, and a
    ///   refused duplicate registration adds nothing.
    /// - Kinds: flat and factor compose as a session-stat effect does ((100 + 10) x 2).
    /// - Null withdraws and Refresh: an Amount answering null after Refresh leaves no modifier; a
    ///   new amount replaces the old one in place; a value where there was none applies.
    /// - Copies: the source token holds only an int and a string; a deep copy of the applied
    ///   modifier added to another stat follows the original while live and reads 0 once the
    ///   contribution is unregistered. A deep copy of a whole stat shows its value at copy time
    ///   until it is marked dirty, and its recalculation then drops the withdrawn contribution.
    /// - After Start: a character's stat change events are raised once per stat one frame after
    ///   its Start, not in the frame of its Start, and not again.
    /// - Unregister withdraws from every target.
    /// </summary>
    internal static class StatContributionsSelfTest
    {
        private const string FlatId = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-flat";
        private const string FactorId = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-factor";
        private static bool _done;

        public static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            _done = true;
            List<string> failures = new List<string>();
            object a = new object(), b = new object(), c = new object();
            try { Run(failures, a, b, c); }
            catch (Exception e) { failures.Add("threw: " + e); }
            finally
            {
                try
                {
                    StatContributions.Unregister(FlatId);
                    StatContributions.Unregister(FactorId);
                    StatContributions.DevRemoveTarget(a);
                    StatContributions.DevRemoveTarget(b);
                    StatContributions.DevRemoveTarget(c);
                }
                catch (Exception e) { failures.Add("cleanup threw: " + e.Message); }
            }
            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: stat contributions passed (validation; once per " +
                                   "target; kinds; null withdraws; Refresh; copies inert after Unregister; " +
                                   "copied stat; after Start; Unregister everywhere).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: stat contributions FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static void Check(List<string> failures, bool ok, string what)
        {
            if (!ok) failures.Add(what);
        }

        private static void Near(List<string> failures, float got, float want, string what)
        {
            if (Math.Abs(got - want) > 0.001f) failures.Add(what + ": " + got + ", expected " + want);
        }

        private static void Run(List<string> failures, object keyA, object keyB, object keyC)
        {
            // Validation.
            Check(failures, !StatContributions.Register(new StatContribution
            {
                Id = "com.example.notloaded/x", Target = StatTarget.Character, Stat = "MaxHitPoints",
                Amount = t => 1f,
            }), "an id naming a plugin that is not loaded was accepted");
            Check(failures, !StatContributions.Register(new StatContribution
            {
                Id = FlatId, Target = StatTarget.Character, Stat = "MaxHitPoints",
            }), "a missing Amount was accepted");

            float? flat = 10f;
            float? factor = 2f;
            Check(failures, StatContributions.Register(new StatContribution
            {
                Id = FlatId, Target = StatTarget.Character, Stat = "MaxHitPoints",
                Kind = SessionStatModifierKind.Flat, Amount = t => flat, Source = "Self-test flat",
            }), "the flat contribution was refused");

            ModifiableFloat statA = new ModifiableFloat(100f);
            ModifiableFloat statB = new ModifiableFloat(100f);

            // Once per target.
            StatContributions.DevSpawn(keyA, StatTarget.Character, statA);
            StatContributions.DevSpawn(keyA, StatTarget.Character, statA);
            Check(failures, statA.Modifiers.Count == 1, "a repeated spawn left " + statA.Modifiers.Count + " modifiers");
            Near(failures, statA.Value, 110f, "flat on spawn");
            Check(failures, !StatContributions.Register(new StatContribution
            {
                Id = FlatId, Target = StatTarget.Character, Stat = "MaxHitPoints",
                Kind = SessionStatModifierKind.Flat, Amount = t => 50f,
            }), "a second registration of a live id was accepted");
            Near(failures, statA.Value, 110f, "after a refused duplicate");

            // Kinds, and a registration applying to a live target.
            StatContributions.Register(new StatContribution
            {
                Id = FactorId, Target = StatTarget.Character, Stat = "MaxHitPoints",
                Kind = SessionStatModifierKind.Factor, Amount = t => factor, Source = "Self-test factor",
            });
            Near(failures, statA.Value, 220f, "flat then factor");

            // Null withdraws; Refresh replaces in place; a value where there was none applies.
            flat = null;
            StatContributions.Refresh(FlatId);
            Near(failures, statA.Value, 200f, "flat withdrawn by null");
            Check(failures, statA.Modifiers.Count == 1, "null left " + statA.Modifiers.Count + " modifiers");
            FloatModifier before = StatContributions.DevModifier(keyA, FactorId);
            factor = 3f;
            StatContributions.Refresh(FactorId);
            Near(failures, statA.Value, 300f, "factor refreshed");
            Check(failures, ReferenceEquals(before, StatContributions.DevModifier(keyA, FactorId)) &&
                            statA.Modifiers.Count == 1, "Refresh did not replace in place");
            flat = 10f;
            StatContributions.Refresh(FlatId);
            Near(failures, statA.Value, 330f, "flat back after null");

            // Copies.
            foreach (FieldInfo f in typeof(LastingSource).GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                                     BindingFlags.NonPublic))
                Check(failures, f.FieldType == typeof(int) || f.FieldType == typeof(string),
                      "the source token holds a " + f.FieldType.Name);
            FloatModifier original = StatContributions.DevModifier(keyA, FactorId);
            FloatModifier copy = original.DeepCopy();
            Check(failures, copy.Source is LastingSource && !ReferenceEquals(copy.Source, original.Source),
                  "the deep copy did not carry its own source token");
            ModifiableFloat other = new ModifiableFloat(50f);
            other.AddModifier(copy);
            Near(failures, other.Value, 150f, "a live copy");

            // After Start: both contributions sit on one stat, so one change event.
            ModifiableFloat statC = new ModifiableFloat(100f);
            int raised = 0;
            StatContributions.DevSpawn(keyC, StatTarget.Character, statC);
            statC.OnValueChange += () => raised++;
            StatContributions.DevStarted(keyC, 1000);
            StatContributions.DevNotify(1000);
            Check(failures, raised == 0, "the after-Start notification ran in the frame of Start");
            StatContributions.DevNotify(1001);
            Check(failures, raised == 1, "the after-Start notification raised " + raised + " changes, expected 1");
            StatContributions.DevNotify(1002);
            Check(failures, raised == 1, "the after-Start notification ran twice");

            // A deep copy of the whole stat.
            Near(failures, statA.Value, 330f, "before copying the stat");
            ModifiableFloat statCopy = statA.DeepCopy();

            // Unregister withdraws everywhere, copies included.
            StatContributions.DevSpawn(keyB, StatTarget.Character, statB);
            Near(failures, statB.Value, 330f, "second target");
            StatContributions.Unregister(FactorId);
            Near(failures, statA.Value, 110f, "first target after Unregister");
            Near(failures, statB.Value, 110f, "second target after Unregister");
            Near(failures, other.Value, 50f, "the copy after Unregister");
            Near(failures, statCopy.Value, 330f, "a copied stat before its next recalculation");
            statCopy.MarkDirty();
            Near(failures, statCopy.Value, 110f, "a copied stat after its next recalculation");
            FloatModifier late = original.DeepCopy();
            ModifiableFloat other2 = new ModifiableFloat(50f);
            other2.AddModifier(late);
            Near(failures, other2.Value, 50f, "a copy made after Unregister");
            Check(failures, !StatContributions.IsRegistered(FactorId), "still registered after Unregister");
        }
    }
}
