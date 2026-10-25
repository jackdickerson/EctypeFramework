using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using CG.Game;
using Client.Utils;
using Gameplay.Utilities;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Self-tests that need a running game, run once per process from the driver in a dev tools
    /// build.
    ///
    /// Timed stat copies. Starts a contribution with a constant curve of 0.25 on a character's
    /// collection, naming a ship-only stat so it changes nothing there, and builds two probe
    /// StatMods bound to that live handle with the contribution's source token:
    /// - an applied-shape probe (initialised), deep-copied through the game's own
    ///   ObjectExtensions.DeepCopy, the copier the perk loader runs on every reapply. The copy must
    ///   return, and a walk of every field reachable from it must find no StatCollection and no
    ///   UnityEngine.Object other than a ScriptableObject (which the copier shares).
    /// - a template probe (not initialised), deep-copied, then initialised as a collection would.
    /// Both copies must read the curve (the applied-shape copy once a collection applies it, which
    /// the test stands in for), and both must read 0 after the contribution ends.
    ///
    /// The probes are never applied to a collection: an applied modifier's AffectedMod leads to the
    /// stat it sits in and from there to every other modifier on it, which is the game's structure,
    /// not this value's.
    /// </summary>
    internal static class DevSelfTest
    {
        private const int WalkLimit = 20000;
        private const float Curve = 0.25f;
        private static bool _done;
        private static float _nextTry;

        public static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;

            StatTagCollection target = AnyCharacterCollection();
            if (target == null) return;
            _done = true;
            TimedStatCopy(target);
        }

        private static StatTagCollection AnyCharacterCollection()
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return null;
            foreach (CG.Game.Player.Player p in game.Players)
                if (p != null && p.Stats != null) return p.Stats;
            return null;
        }

        private static void TimedStatCopy(StatTagCollection target)
        {
            TimedStatContribution c = null;
            try
            {
                c = TimedStatContributions.Start("ectype.dev.copytest", target,
                                                 FrameworkStats.IncomingProjectileSpeed,
                                                 ModifierType.AdditiveMultiplier, u => Curve, 5f,
                                                 PhotonNetwork.ServerTimestamp, null,
                                                 "Ectype copy self-test");
                if (c == null)
                {
                    Plugin.Log.LogWarning("Ectype: dev self-test: timed stat copy NOT RUN - the contribution did not start.");
                    return;
                }

                StatMod applied = Probe(c);
                applied.InitDynamicElements();
                StatMod appliedCopy = applied.DeepCopy();
                StatMod template = Probe(c);
                StatMod templateCopy = template.DeepCopy();
                if (appliedCopy == null || templateCopy == null)
                {
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - DeepCopy returned null.");
                    return;
                }

                string found;
                int walked = Walk(appliedCopy, out found);
                if (found != null)
                {
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - the copy reaches " +
                                        found + " (" + walked + " objects walked).");
                    return;
                }
                if (walked >= WalkLimit)
                {
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - the copy's graph " +
                                        "exceeds " + WalkLimit + " objects.");
                    return;
                }

                // What a collection does when it applies each copy.
                TimedStatContributions.OnApplied(appliedCopy);
                templateCopy.InitDynamicElements();
                float during1 = Amount(appliedCopy), during2 = Amount(templateCopy);

                TimedStatContributions.End(c, "self-test done");
                c = null;
                float after1 = Amount(appliedCopy), after2 = Amount(templateCopy);

                bool ok = Near(during1, Curve) && Near(during2, Curve) && Near(after1, 0f) && Near(after2, 0f);
                string amounts = "copy of applied value " + during1.ToString("0.###") + " then " +
                                 after1.ToString("0.###") + ", copy of template " + during2.ToString("0.###") +
                                 " then " + after2.ToString("0.###") + " (expected " + Curve + " then 0)";
                if (ok)
                    Plugin.Log.LogInfo("Ectype: dev self-test: timed stat copy passed - DeepCopy returned; " +
                                       walked + " objects walked, no StatCollection, no Unity object " +
                                       "other than a ScriptableObject; " + amounts + ".");
                else
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - " + amounts + ".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - threw: " + e);
            }
            finally
            {
                if (c != null) TimedStatContributions.End(c, "self-test done");
            }
        }

        private static StatMod Probe(TimedStatContribution c)
        {
            StatMod probe = new StatMod(new FloatModifier(0f, ModifierType.AdditiveMultiplier), c.StatId)
            {
                DynamicValue = new CurveValue(c.Handle, c.Label)
            };
            probe.SetSource(c.Source);
            return probe;
        }

        private static float Amount(StatMod mod)
        {
            FloatModifier f = mod.Mod as FloatModifier;
            return f != null ? f.Amount : float.NaN;
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }

        /// <summary>Breadth-first over every instance field, base types included. Returns the
        /// number of objects visited; <paramref name="found"/> names the first forbidden one
        /// with its field path.</summary>
        private static int Walk(object root, out string found)
        {
            found = null;
            HashSet<object> seen = new HashSet<object>(RefComparer.Instance);
            Queue<KeyValuePair<object, string>> queue = new Queue<KeyValuePair<object, string>>();
            queue.Enqueue(new KeyValuePair<object, string>(root, root.GetType().Name));
            while (queue.Count > 0 && seen.Count < WalkLimit)
            {
                KeyValuePair<object, string> next = queue.Dequeue();
                object o = next.Key;
                if (o == null || !seen.Add(o)) continue;
                Type t = o.GetType();
                if (t.IsPrimitive || o is string || t.IsEnum || o is Delegate) continue;

                if (o is StatCollection)
                {
                    found = "a " + t.Name + " at " + next.Value;
                    return seen.Count;
                }
                if (o is UnityEngine.Object)
                {
                    if (o is ScriptableObject) continue;
                    found = "a " + t.Name + " (Unity object) at " + next.Value;
                    return seen.Count;
                }

                if (o is IEnumerable && !(o is string))
                {
                    int i = 0;
                    foreach (object item in (IEnumerable)o)
                    {
                        if (item != null) queue.Enqueue(new KeyValuePair<object, string>(item, next.Value + "[" + i + "]"));
                        i++;
                    }
                }

                for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                {
                    foreach (FieldInfo f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                          BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType.IsPrimitive) continue;
                        object v;
                        try { v = f.GetValue(o); }
                        catch { continue; }
                        if (v != null) queue.Enqueue(new KeyValuePair<object, string>(v, next.Value + "." + f.Name));
                    }
                }
            }
            return seen.Count;
        }

        private sealed class RefComparer : IEqualityComparer<object>
        {
            internal static readonly RefComparer Instance = new RefComparer();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
    }
}
