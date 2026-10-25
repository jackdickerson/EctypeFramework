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
    /// Timed stat copy safety: starts a contribution with a zero curve on the first stat
    /// collection available (the player ship's, else a character's), builds a StatMod carrying
    /// a CurveValue bound to that live handle with the contribution's source token, initialises
    /// it, and deep-copies it through the game's own ObjectExtensions.DeepCopy, the copier the
    /// perk loader runs on every reapply. The copy must return, and a walk of every field
    /// reachable from it must find no StatCollection and no UnityEngine.Object other than a
    /// ScriptableObject (which the copier shares rather than clones).
    ///
    /// The probe is never applied to a collection: an applied modifier's AffectedMod leads to
    /// the stat it sits in and from there to every other modifier on it, which is the game's
    /// structure, not this value's. A copier sees the StatMod before application.
    /// </summary>
    internal static class DevSelfTest
    {
        private const int WalkLimit = 20000;
        private static bool _done;
        private static float _nextTry;

        public static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;

            StatTagCollection target = AnyCollection();
            if (target == null) return;
            _done = true;
            TimedStatCopy(target);
        }

        private static StatTagCollection AnyCollection()
        {
            ClientGame game = ClientGame.Current;
            if (game == null) return null;
            if (game.PlayerShip != null && game.PlayerShip.Stats != null) return game.PlayerShip.Stats;
            if (game.Players != null)
                foreach (CG.Game.Player.Player p in game.Players)
                    if (p != null && p.Stats != null) return p.Stats;
            return null;
        }

        private static void TimedStatCopy(StatTagCollection target)
        {
            TimedStatContribution c = null;
            try
            {
                c = TimedStatContributions.Start("ectype.dev.copytest", target, "MaxHitPoints",
                                                 ModifierType.AdditiveMultiplier, u => 0f, 2f,
                                                 PhotonNetwork.ServerTimestamp, null,
                                                 "Ectype copy self-test");
                if (c == null)
                {
                    Plugin.Log.LogWarning("Ectype: dev self-test: timed stat copy NOT RUN - the contribution did not start.");
                    return;
                }

                StatMod probe = new StatMod(new FloatModifier(0f, ModifierType.AdditiveMultiplier), c.StatId)
                {
                    DynamicValue = new CurveValue(c.Handle, c.Label)
                };
                probe.SetSource(c.Source);
                probe.InitDynamicElements();

                StatMod copy = probe.DeepCopy();
                if (copy == null)
                {
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - DeepCopy returned null.");
                    return;
                }

                string found;
                int walked = Walk(copy, out found);
                if (found != null)
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - the copy reaches " +
                                        found + " (" + walked + " objects walked).");
                else if (walked >= WalkLimit)
                    Plugin.Log.LogError("Ectype: dev self-test: timed stat copy FAILED - the copy's graph " +
                                        "exceeds " + WalkLimit + " objects.");
                else
                    Plugin.Log.LogInfo("Ectype: dev self-test: timed stat copy passed - DeepCopy returned; " +
                                       walked + " objects walked, no StatCollection, no Unity object " +
                                       "other than a ScriptableObject.");
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
