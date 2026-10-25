using System;
using System.Collections.Generic;

using CG.Space;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    /// <summary>Which game objects a lasting stat contribution applies to.</summary>
    public enum StatTarget
    {
        /// <summary>Every player ship (PlayerControlledShip), including extra ships another mod
        /// spawns through the game's loadout path.</summary>
        PlayerShip,
        /// <summary>Every player character (Player): the local one and every replica.</summary>
        Character,
    }

    /// <summary>
    /// A modifier a mod puts on one stat of every ship or every character for as long as that
    /// object lives. Registered on every client; applied on every client to every target as it
    /// spawns, because stat modifiers are not replicated.
    /// </summary>
    public sealed class StatContribution
    {
        /// <summary><c>"&lt;plugin GUID&gt;/&lt;name&gt;"</c>. Unique among registered
        /// contributions.</summary>
        public string Id;

        /// <summary>The objects it applies to.</summary>
        public StatTarget Target;

        /// <summary>A vanilla StatType name, a framework stat name, a mod stat's
        /// <c>"&lt;owner&gt;/Name"</c>, or a raw id. The target's own collection must register
        /// it.</summary>
        public string Stat;

        /// <summary>How the amount composes: the same mapping as a session-stat effect.</summary>
        public SessionStatModifierKind Kind;

        /// <summary>
        /// The amount on one target (a PlayerControlledShip or a Player), or null for no
        /// contribution there. Asked at the target's spawn and on every
        /// <see cref="StatContributions.Refresh"/>. Every client asks it about every target, so it
        /// must answer alike on every client: read replicated state only.
        /// </summary>
        public Func<OrbitObject, float?> Amount;

        /// <summary>The label a stat display shows for this modifier. Defaults to the id.</summary>
        public string Source;
    }

    /// <summary>
    /// Lasting stat contributions: one FloatModifier per (target, contribution) on the target's
    /// own stat, for the target's lifetime.
    ///
    /// Spawn points. A player ship is caught in a postfix on
    /// GameSession.ApplyGlobalOrbitObjectSpawnProperties, which OrbitObject.Start calls on every
    /// client for every orbit object; the lines right after it set EstimatedHitPoints to
    /// MaxHitPoints on every client, and HitPoints on the master client, which replicates it. A
    /// contribution to MaxHitPoints is therefore where the ship starts. A character is caught in
    /// the Player.Awake postfix, after the framework registers its stat surfaces there.
    ///
    /// Own stat only. The modifier goes on the target collection's own ModifiableFloat, not
    /// through StatTagCollection.ApplyModifiers, which recurses into every child collection: a
    /// ship's modules and a ship's central computer are child collections and register their
    /// own MaxHitPoints, so a recursed hull modifier would scale every module too.
    ///
    /// Copy safety. The modifier's source holds an integer handle and a label, nothing else, so a
    /// deep copy of the modifier stops there. A copy is found when the stat it was added to
    /// recalculates (a prefix on ModifiableFloat.RecalculateValue, live only once a handle has
    /// been issued): while the handle is live the copy is tracked and follows the original's
    /// amount; once it is withdrawn every tracked copy is set to 0, which every modifier type
    /// composes as no change, and an untracked copy reads 0 at its stat's next recalculation.
    /// </summary>
    public static class StatContributions
    {
        /// <summary>Seconds between the driver's checks for destroyed targets and a changed
        /// session.</summary>
        public const float PruneSeconds = 1f;

        private sealed class Entry
        {
            internal string Id;
            internal StatTarget Target;
            internal string Stat;
            internal SessionStatModifierKind Kind;
            internal Func<OrbitObject, float?> Amount;
            internal string Label;
            internal int StatId;
        }

        internal sealed class Applied
        {
            internal string Id;
            internal int Handle;
            internal LastingSource Source;
            internal FloatModifier Mod;
            internal ModifiableFloat Stat;
            internal StatTagCollection Collection;
            internal float Requested;
            internal readonly List<WeakReference> Copies = new List<WeakReference>();
        }

        internal sealed class TargetRec
        {
            /// <summary>Identity for the once-per-target rule: the object itself, or a dev
            /// self-test key.</summary>
            internal object Key;
            internal OrbitObject Obj;
            internal bool IsUnity;
            internal StatTarget Kind;
            internal GameSession Session;
            internal string Name;
            /// <summary>Dev self-test only: the stat every contribution lands on.</summary>
            internal ModifiableFloat TestStat = null;
            internal readonly Dictionary<string, Applied> ById = new Dictionary<string, Applied>(StringComparer.Ordinal);

            internal bool Destroyed { get { return IsUnity && Obj == null; } }
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<TargetRec> Targets = new List<TargetRec>();
        private static readonly Dictionary<int, Applied> ByHandle = new Dictionary<int, Applied>();
        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();
        private static bool _anyHandle;
        private static bool _loggedFirst;
        private static float _nextPrune;

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Register a contribution, on every client, before targets spawn (plugin Awake). It is
        /// applied to every target that spawns from then on, and at once to every live target
        /// the framework has already seen. Returns false, having registered nothing, when the
        /// contribution is incomplete or its id is already registered.
        /// </summary>
        public static bool Register(StatContribution contribution)
        {
            try
            {
                string id = contribution != null ? contribution.Id : null;
                // TODO(J7): check the "<plugin GUID>/<name>" shape with J7's shared identifier helper.
                if (string.IsNullOrEmpty(id))
                {
                    Plugin.Log.LogWarning("Ectype: stat contribution not registered: it needs an id " +
                                          "(\"<plugin GUID>/<name>\").");
                    return false;
                }
                if (Find(id) != null)
                {
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + id + "' not registered: that id " +
                                          "is already registered; unregister it first.");
                    return false;
                }
                if (string.IsNullOrEmpty(contribution.Stat) || contribution.Amount == null)
                {
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + id + "' not registered: it needs " +
                                          "a stat and an Amount function.");
                    return false;
                }
                if (contribution.Target != StatTarget.PlayerShip && contribution.Target != StatTarget.Character)
                {
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + id + "' not registered: unknown " +
                                          "target " + (int)contribution.Target + ".");
                    return false;
                }
                if (contribution.Kind != SessionStatModifierKind.Flat &&
                    contribution.Kind != SessionStatModifierKind.Percent &&
                    contribution.Kind != SessionStatModifierKind.Factor)
                {
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + id + "' not registered: unknown " +
                                          "kind " + (int)contribution.Kind + ".");
                    return false;
                }

                Entry e = new Entry
                {
                    Id = id,
                    Target = contribution.Target,
                    Stat = contribution.Stat,
                    Kind = contribution.Kind,
                    Amount = contribution.Amount,
                    Label = string.IsNullOrEmpty(contribution.Source) ? id : contribution.Source,
                };
                Entries.Add(e);
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: stat contribution '" + id + "' registered: " + e.Target + " " +
                                       e.Stat + " " + e.Kind + ".");
                ApplyToLive(e);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Ectype: registering stat contribution '" +
                                    (contribution != null ? contribution.Id : "(null)") + "' failed: " + ex);
                return false;
            }
        }

        /// <summary>Ask <see cref="StatContribution.Amount"/> again on every live target and
        /// apply the answers: a new amount replaces the old one in place, null withdraws, a value
        /// where there was none applies. Returns how many targets were asked.</summary>
        public static int Refresh(string id)
        {
            try
            {
                Entry e = Find(id);
                if (e == null)
                {
                    if (WarnedOnce.Add("refresh:" + id))
                        Plugin.Log.LogWarning("Ectype: stat contribution '" + id + "' refreshed but not registered.");
                    return 0;
                }
                return ApplyToLive(e);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Ectype: refreshing stat contribution '" + id + "' failed: " + ex);
                return 0;
            }
        }

        /// <summary>Withdraw a contribution from every target and forget it. Returns false when it
        /// was not registered.</summary>
        public static bool Unregister(string id)
        {
            try
            {
                Entry e = Find(id);
                if (e == null) return false;
                Entries.Remove(e);
                for (int i = 0; i < Targets.Count; i++)
                {
                    Applied a;
                    if (Targets[i].ById.TryGetValue(id, out a)) Withdraw(Targets[i], a);
                }
                if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: stat contribution '" + id + "' unregistered.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Ectype: unregistering stat contribution '" + id + "' failed: " + ex);
                return false;
            }
        }

        /// <summary>True while a contribution with this id is registered.</summary>
        public static bool IsRegistered(string id) { return Find(id) != null; }

        /// <summary>The amount <see cref="StatContribution.Amount"/> last answered for this target,
        /// or null when the contribution is not applied there.</summary>
        public static float? Current(OrbitObject target, string id)
        {
            TargetRec t = RecOf(target);
            Applied a;
            return t != null && id != null && t.ById.TryGetValue(id, out a) ? a.Requested : (float?)null;
        }

        // ------------------------------------------------------------------ spawn and lifetime

        /// <summary>A target spawned. Once per object: a repeated call changes nothing.</summary>
        internal static void OnSpawn(OrbitObject obj, StatTarget kind)
        {
            if (obj == null) return;
            Spawn(new TargetRec
            {
                Key = obj,
                Obj = obj,
                IsUnity = true,
                Kind = kind,
                // A ship is caught in Start, where the active session is its own. A character is
                // caught in Awake, which can run before the session it belongs to is active; its
                // session is read at its Start (OnStarted).
                Session = kind == StatTarget.PlayerShip ? GameSessionManager.ActiveSession : null,
                Name = obj.name,
            });
        }

        /// <summary>An orbit object reached its Start: a character caught at Awake takes the
        /// session that is active now as its own.</summary>
        internal static void OnStarted(OrbitObject obj)
        {
            TargetRec t = RecOf(obj);
            if (t != null && t.Session == null) t.Session = GameSessionManager.ActiveSession;
        }

        private static void Spawn(TargetRec t)
        {
            if (RecOf(t.Key) != null) return;
            Targets.Add(t);
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Target == t.Kind) Apply(t, Entries[i]);
        }

        /// <summary>Driven from EctypeDriver.Update: forgets destroyed targets (their stats are
        /// not touched) and withdraws everything from targets of an ended session.</summary>
        internal static void Poll()
        {
            if (Targets.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextPrune) return;
            _nextPrune = now + PruneSeconds;
            GameSession session = GameSessionManager.ActiveSession;
            for (int i = Targets.Count - 1; i >= 0; i--)
            {
                TargetRec t = Targets[i];
                if (t.TestStat != null) continue;
                if (t.Destroyed) Drop(t, "target destroyed");
                else if (t.Session != null && !ReferenceEquals(t.Session, session)) Drop(t, "session ended");
            }
        }

        private static void Drop(TargetRec t, string reason)
        {
            Targets.Remove(t);
            List<Applied> all = new List<Applied>(t.ById.Values);
            foreach (Applied a in all) Withdraw(t, a);
            if (Plugin.Verbose && all.Count > 0)
                Plugin.Log.LogInfo("Ectype: stat contributions on " + t.Name + " dropped (" + reason + ").");
        }

        // ------------------------------------------------------------------ apply and withdraw

        private static int ApplyToLive(Entry e)
        {
            int n = 0;
            GameSession session = GameSessionManager.ActiveSession;
            for (int i = 0; i < Targets.Count; i++)
            {
                TargetRec t = Targets[i];
                if (t.Kind != e.Target || t.Destroyed) continue;
                if (t.TestStat == null && t.Session != null && !ReferenceEquals(t.Session, session)) continue;
                Apply(t, e);
                n++;
            }
            return n;
        }

        /// <summary>Asks the amount and brings the target's modifier to it. Never throws.</summary>
        private static void Apply(TargetRec t, Entry e)
        {
            try
            {
                float? want = Ask(e, t);
                Applied a;
                t.ById.TryGetValue(e.Id, out a);
                if (want == null)
                {
                    if (a != null) Withdraw(t, a);
                    return;
                }
                ModifierType type;
                float amount;
                SessionStatMutators.MapKind(e.Kind, want.Value, out type, out amount);
                if (a != null)
                {
                    a.Requested = want.Value;
                    if (a.Mod.Amount != amount) SetAmount(a, amount);
                    return;
                }

                ModifiableFloat stat;
                StatTagCollection collection;
                if (!StatOf(t, e, out stat, out collection)) return;
                a = new Applied
                {
                    Id = e.Id,
                    Handle = TimedStatContributions.NextHandle(),
                    Stat = stat,
                    Collection = collection,
                    Requested = want.Value,
                };
                a.Source = new LastingSource(a.Handle, e.Label);
                a.Mod = new FloatModifier(amount, type, a.Source);
                ByHandle[a.Handle] = a;
                _anyHandle = true;
                t.ById[e.Id] = a;
                stat.AddModifier(a.Mod);
                if (collection != null) collection.OnModifiersChange?.Invoke();

                string line = "Ectype: stat contribution '" + e.Id + "' applied to " + t.Name + ": " + e.Stat +
                              " " + e.Kind + " " + want.Value.ToString("0.###") + " (now " +
                              stat.Value.ToString("0.###") + ").";
                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Plugin.Log.LogInfo(line + " Further applications log under [Debug] VerboseLogging.");
                }
                else if (Plugin.Verbose)
                {
                    Plugin.Log.LogInfo(line);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Ectype: applying stat contribution '" + e.Id + "' to " + t.Name + " failed: " + ex);
            }
        }

        /// <summary>The caller's amount, or null. A throw or a non-finite answer is reported once
        /// per contribution and counts as null; so does a factor at or below 0.</summary>
        private static float? Ask(Entry e, TargetRec t)
        {
            float? v;
            try { v = e.Amount(t.Obj); }
            catch (Exception ex)
            {
                if (WarnedOnce.Add("threw:" + e.Id))
                    Plugin.Log.LogError("Ectype: stat contribution '" + e.Id + "' Amount threw on " + t.Name +
                                        "; it contributes nothing there (reported once): " + ex);
                return null;
            }
            if (v == null) return null;
            float f = v.Value;
            if (float.IsNaN(f) || float.IsInfinity(f))
            {
                if (WarnedOnce.Add("nonfinite:" + e.Id))
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + e.Id + "' Amount is not a finite " +
                                          "number on " + t.Name + "; it contributes nothing there (reported once).");
                return null;
            }
            if (e.Kind == SessionStatModifierKind.Factor && !(f > 0f))
            {
                if (WarnedOnce.Add("factor:" + e.Id))
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + e.Id + "' is a factor and Amount " +
                                          "answered " + f.ToString("R") + "; a factor is above 0, so it contributes " +
                                          "nothing there (reported once).");
                return null;
            }
            return f;
        }

        /// <summary>The target's own float stat the contribution names, or false (warned once)
        /// when the stat does not resolve or the target's own collection does not register it as
        /// a float.</summary>
        private static bool StatOf(TargetRec t, Entry e, out ModifiableFloat stat, out StatTagCollection collection)
        {
            stat = null;
            collection = null;
            if (t.TestStat != null) { stat = t.TestStat; return true; }
            if (e.StatId == 0)
            {
                try
                {
                    FrameworkStats.EnsureRegistered();
                    e.StatId = StatRegistry.Resolve(e.Stat, null);
                }
                catch (Exception ex)
                {
                    if (WarnedOnce.Add("resolve:" + e.Id))
                        Plugin.Log.LogWarning("Ectype: stat contribution '" + e.Id + "' names a stat that does " +
                                              "not resolve; it contributes nothing: " + ex.Message);
                    return false;
                }
            }
            collection = t.Obj.Stats;
            StatBase sb;
            if (collection != null && collection.Stats != null && collection.Stats.TryGetValue(e.StatId, out sb))
                stat = sb.Data as ModifiableFloat;
            if (stat == null)
            {
                if (WarnedOnce.Add("nostat:" + e.Id + ":" + t.Obj.GetType().Name))
                    Plugin.Log.LogWarning("Ectype: stat contribution '" + e.Id + "' names " + e.Stat + ", which " +
                                          t.Obj.GetType().Name + "'s own stat collection does not register as " +
                                          "a float stat; it has no effect there.");
                return false;
            }
            return true;
        }

        private static void SetAmount(Applied a, float amount)
        {
            a.Mod.Amount = amount;
            Touch(a.Stat);
            if (a.Collection != null) a.Collection.OnModifiersChange?.Invoke();
            SetCopies(a, amount);
        }

        /// <summary>Removes the modifier from its target (unless the target is destroyed) and
        /// sets it and every tracked copy to 0, which every modifier type composes as no change.</summary>
        private static void Withdraw(TargetRec t, Applied a)
        {
            t.ById.Remove(a.Id);
            ByHandle.Remove(a.Handle);
            a.Mod.Amount = 0f;
            if (!t.Destroyed)
            {
                try
                {
                    a.Stat.RemoveModsFromSource(a.Source);
                    if (a.Collection != null) a.Collection.OnModifiersChange?.Invoke();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Ectype: withdrawing stat contribution '" + a.Id + "' from " + t.Name +
                                        " failed: " + ex);
                }
            }
            SetCopies(a, 0f);
            a.Copies.Clear();
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: stat contribution '" + a.Id + "' withdrawn from " + t.Name + ".");
        }

        private static void SetCopies(Applied a, float amount)
        {
            for (int i = a.Copies.Count - 1; i >= 0; i--)
            {
                FloatModifier m = a.Copies[i].Target as FloatModifier;
                if (m == null) { a.Copies.RemoveAt(i); continue; }
                if (m.Amount == amount) continue;
                m.Amount = amount;
                try { Touch(m.AffectedMod as ModifiableFloat); }
                catch (Exception ex)
                {
                    a.Copies.RemoveAt(i);
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: stat contribution '" + a.Id + "': updating a copy threw; " +
                                           "the copy is no longer followed: " + ex.Message);
                }
            }
        }

        private static void Touch(ModifiableFloat stat)
        {
            if (stat == null) return;
            stat.MarkDirty();
            stat.OnValueChange?.Invoke();
        }

        // ------------------------------------------------------------------ copies

        /// <summary>True once any handle has been issued this process: the recalculation prefix
        /// has nothing to do before that.</summary>
        internal static bool AnyHandle { get { return _anyHandle; } }

        private static bool _copyCheckFailed;

        internal static void CopyCheckFailed(Exception e)
        {
            if (_copyCheckFailed) return;
            _copyCheckFailed = true;
            Plugin.Log.LogError("Ectype: stat contribution copy check on recalculation failed (reported once): " + e);
        }

        /// <summary>
        /// A float stat is recalculating. Any modifier whose source is a lasting-contribution token
        /// other than the original is a copy: with its handle live it is tracked and takes the
        /// original's amount; with its handle withdrawn it is set to 0.
        /// </summary>
        internal static void OnRecalculate(List<FloatModifier> mods)
        {
            if (mods == null) return;
            for (int i = 0; i < mods.Count; i++)
            {
                FloatModifier m = mods[i];
                LastingSource s = m != null ? m.Source as LastingSource : null;
                if (s == null) continue;
                Applied a;
                if (ByHandle.TryGetValue(s.Handle, out a))
                {
                    if (ReferenceEquals(m, a.Mod)) continue;
                    Track(a, m);
                    if (m.Amount != a.Mod.Amount) m.Amount = a.Mod.Amount;
                }
                else if (m.Amount != 0f)
                {
                    m.Amount = 0f;
                }
            }
        }

        private static void Track(Applied a, FloatModifier copy)
        {
            for (int i = 0; i < a.Copies.Count; i++)
                if (ReferenceEquals(a.Copies[i].Target, copy)) return;
            a.Copies.Add(new WeakReference(copy));
        }

        /// <summary>The label and state behind a handle, for the source token.</summary>
        internal static string Describe(int handle)
        {
            Applied a;
            return ByHandle.TryGetValue(handle, out a)
                ? "lasting, " + a.Requested.ToString("0.###")
                : "withdrawn";
        }

        // ------------------------------------------------------------------ helpers

        private static Entry Find(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Entries.Count; i++)
                if (string.Equals(Entries[i].Id, id, StringComparison.Ordinal)) return Entries[i];
            return null;
        }

        private static TargetRec RecOf(object obj)
        {
            if (obj == null) return null;
            for (int i = 0; i < Targets.Count; i++)
                if (ReferenceEquals(Targets[i].Key, obj)) return Targets[i];
            return null;
        }

#if ECTYPE_DEV
        /// <summary>Dev self-test: spawns a target with no game object behind it, identified by
        /// <paramref name="key"/>, whose contributions land on <paramref name="stat"/>. A second
        /// call with the same key is a repeated spawn. Never pruned; remove it with
        /// <see cref="DevRemoveTarget"/>.</summary>
        internal static void DevSpawn(object key, StatTarget kind, ModifiableFloat stat)
        {
            Spawn(new TargetRec { Key = key, Kind = kind, TestStat = stat, Name = "dev target", IsUnity = false });
        }

        internal static void DevRemoveTarget(object key)
        {
            TargetRec t = RecOf(key);
            if (t != null) Drop(t, "dev self-test");
        }

        internal static FloatModifier DevModifier(object key, string id)
        {
            TargetRec t = RecOf(key);
            Applied a;
            return t != null && t.ById.TryGetValue(id, out a) ? a.Mod : null;
        }
#endif
    }

    /// <summary>
    /// The lasting modifier's source and removal key. It carries only the handle and the label,
    /// so a deep copy of the modifier stops here. Stat displays that list a stat's modifiers read
    /// the label through the game's descriptive-source interface.
    /// </summary>
    internal sealed class LastingSource : IDescriptiveModifierSource
    {
        internal readonly int Handle;
        internal readonly string Label;

        internal LastingSource(int handle, string label) { Handle = handle; Label = label; }

        public string GetHeader() { return Label; }

        public string GetDescription() { return StatContributions.Describe(Handle); }

        public override string ToString() { return "Ectype stat contribution " + Label; }
    }

    /// <summary>
    /// Postfix: OrbitObject.Start calls this on every client for every orbit object, and the lines
    /// right after it set the object's hit points to its maximum. A player ship's lasting
    /// contributions are applied here, so a MaxHitPoints contribution is where the ship starts. A
    /// character, applied at Awake, takes its session here.
    /// </summary>
    [HarmonyPatch(typeof(GameSession), "ApplyGlobalOrbitObjectSpawnProperties")]
    internal static class GameSession_ApplyGlobalOrbitObjectSpawnProperties_Patch
    {
        private static void Postfix(OrbitObject target)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try
            {
                PlayerControlledShip ship = target as PlayerControlledShip;
                if (ship != null) StatContributions.OnSpawn(ship, StatTarget.PlayerShip);
                else if (target is CG.Game.Player.Player) StatContributions.OnStarted(target);
            }
            catch (Exception e) { Plugin.Log.LogError("Ectype: stat contributions on a spawning ship failed: " + e); }
        }
    }

    /// <summary>
    /// Prefix: a float stat about to recalculate is where a copy of a lasting modifier is found.
    /// Nothing to do until the first lasting modifier has been applied.
    /// </summary>
    [HarmonyPatch(typeof(ModifiableFloat), "RecalculateValue")]
    internal static class ModifiableFloat_RecalculateValue_StatContributions
    {
        private static void Prefix(ModifiableFloat __instance)
        {
            if (!StatContributions.AnyHandle) return;
            try { StatContributions.OnRecalculate(__instance.Modifiers); }
            catch (Exception e) { StatContributions.CopyCheckFailed(e); }
        }
    }
}
