using System;
using System.Collections.Generic;

using Gameplay.Utilities;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// One live contribution: the modifier's source, so the removal key for the one StatMod it
    /// applied, and the handle a caller keeps to read or end it.
    /// </summary>
    public sealed class TimedStatContribution : IDescriptiveModifierSource
    {
        /// <summary>Identity on its target: starting the same key on the same collection again
        /// ends this one first.</summary>
        public readonly string Key;
        /// <summary>The name shown for this contribution in the stat tooltip.</summary>
        public readonly string Label;
        /// <summary>The stat collection the modifier is applied to.</summary>
        public readonly StatTagCollection Target;
        /// <summary>The id of the stat it modifies.</summary>
        public readonly int StatId;
        /// <summary>When it started, on PhotonNetwork.ServerTimestamp.</summary>
        public readonly int StartServerMs;
        /// <summary>How long it runs, in seconds.</summary>
        public readonly float DurationSeconds;

        internal readonly int DurationMs;
        internal readonly Func<float, float> AmountAt;
        internal readonly Func<bool> KeepAlive;
        internal readonly UnityEngine.Object Holder;
        internal readonly bool HolderIsUnity;
        internal readonly GameSession Session;
        internal FloatModifier Mod;
        internal CurveValue Value;
        internal bool Ended;
        internal bool Faulted;
        internal string Reason;

        internal TimedStatContribution(string key, string label, StatTagCollection target, int statId,
                                       int startServerMs, int durationMs, Func<float, float> amountAt,
                                       Func<bool> keepAlive)
        {
            Key = key;
            Label = string.IsNullOrEmpty(label) ? key : label;
            Target = target;
            StatId = statId;
            StartServerMs = startServerMs;
            DurationMs = durationMs;
            DurationSeconds = durationMs / 1000f;
            AmountAt = amountAt;
            KeepAlive = keepAlive;
            Holder = target.Holder as UnityEngine.Object;
            HolderIsUnity = !ReferenceEquals(Holder, null);
            Session = GameSessionManager.ActiveSession;
        }

        /// <summary>True until the contribution ends.</summary>
        public bool Active { get { return !Ended; } }

        /// <summary>Why it ended ("expired", "replaced", "session ended", "target destroyed",
        /// "keep-alive returned false", or the caller's reason), null while active.</summary>
        public string EndReason { get { return Reason; } }

        /// <summary>Elapsed fraction of the duration on the server clock, 0 to 1.</summary>
        public float Progress
        {
            get { return TimedStatContributions.ProgressAt(StartServerMs, DurationMs, PhotonNetwork.ServerTimestamp); }
        }

        /// <summary>The amount the modifier carried at its last update.</summary>
        public float Amount { get { return Mod != null ? Mod.Amount : 0f; } }

        /// <summary>The curve at the current progress. Never throws: it runs inside the game's own
        /// ApplyModifiers and its dynamic-value recalculation.</summary>
        internal float AmountNow()
        {
            try
            {
                float a = AmountAt(Progress);
                return float.IsNaN(a) || float.IsInfinity(a) ? 0f : a;
            }
            catch (Exception e)
            {
                if (!Faulted)
                {
                    Faulted = true;
                    Plugin.Log.LogError("Ectype: timed stat '" + Key + "' amount function threw; it " +
                                        "contributes 0 and ends at the next update: " + e);
                }
                return 0f;
            }
        }

        /// <summary>The tooltip header: the contribution's label.</summary>
        public string GetHeader() { return Label; }

        /// <summary>The tooltip line: the current amount and how much of the duration has elapsed.</summary>
        public string GetDescription()
        {
            return Amount.ToString("0.###") + ", " + (Progress * 100f).ToString("0") + "% of " +
                   DurationSeconds.ToString("0.#") + " s elapsed";
        }
    }

    /// <summary>
    /// A stat modifier whose amount follows a curve over a fixed duration, on any StatTagCollection.
    ///
    /// The amount is a function of progress u (elapsed / duration, 0 to 1), with elapsed measured on
    /// PhotonNetwork.ServerTimestamp from a start stamp the caller passes. It reaches the stat through
    /// the game's own dynamic-value path: one StatMod carrying a ModDynamicValue, applied with
    /// ApplyModifiers(mods, contribution, true). On each update the value's sealed RecalculateValue
    /// rewrites the modifier's Amount in place and raises OnValueChange, which every collection the
    /// mod reached subscribed to; each one that registers the stat marks it dirty and raises its
    /// OnModifiersChange. No modifier list changes until the end, which is one RemoveModifier by
    /// source: it removes the mod from every collection in the subtree and unsubscribes it.
    ///
    /// Updates run ten times a second from the framework driver. With nothing live the driver step is
    /// one count check.
    ///
    /// Nothing is networked. A machine runs only what it started. Every machine that starts the same
    /// contribution from the same replicated signal and stamp computes the same phase, whenever the
    /// signal reached it. A machine that never saw the signal (a joiner who arrived after it) runs
    /// none, and nothing here catches it up.
    ///
    /// The statuses fix every amount when applied and bind to an OrbitObject (broadcast) or a Player
    /// (local clock), so this is its own layer; it shares their stat resolution, StatRegistry.
    /// </summary>
    public static class TimedStatContributions
    {
        /// <summary>Seconds between amount updates.</summary>
        public const float UpdateSeconds = 0.1f;

        private static readonly List<TimedStatContribution> Live = new List<TimedStatContribution>();
        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();
        private static float _nextUpdate;
        private static bool _loggedFirst;

        /// <summary>A curve that falls linearly from <paramref name="startAmount"/> at u = 0 to 0 at
        /// u = 1.</summary>
        public static Func<float, float> LinearDecay(float startAmount)
        {
            return u => startAmount * (1f - u);
        }

        /// <summary>
        /// Start a contribution, or restart it: a live one with the same key on the same collection
        /// ends first ("replaced"), so restarts never stack. Returns null, having applied nothing,
        /// when an argument is invalid, the stat name does not resolve, the window is already over on
        /// this machine's clock, or the framework is inert.
        /// </summary>
        /// <param name="key">Identity on the target; namespace it with the owning mod.</param>
        /// <param name="target">The collection the StatMod is applied to; it recurses into the
        /// children as any modifier does. A target whose holder is a Unity object ends without
        /// being touched once that object is destroyed.</param>
        /// <param name="statAlias">A vanilla StatType name, a framework stat name, or a raw id.</param>
        /// <param name="type">How the amount composes. A factor f is ExponentialMultiplier with an
        /// amount of f - 1.</param>
        /// <param name="amountAt">The amount at progress u, 0 to 1. Called at each update; keep it
        /// allocation-free.</param>
        /// <param name="durationSeconds">Length of the window.</param>
        /// <param name="startServerMs">Start of the window on PhotonNetwork.ServerTimestamp. A
        /// stamp in the past starts part-way along the curve; one ahead of this machine's clock
        /// starts now.</param>
        /// <param name="keepAlive">Polled at each update; false ends the contribution.</param>
        /// <param name="label">Tooltip header and log name; defaults to the key.</param>
        public static TimedStatContribution Start(string key, StatTagCollection target, string statAlias,
                                                  ModifierType type, Func<float, float> amountAt,
                                                  float durationSeconds, int startServerMs,
                                                  Func<bool> keepAlive = null, string label = null)
        {
            TimedStatContribution c = null;
            try
            {
                if (string.IsNullOrEmpty(key) || target == null || amountAt == null ||
                    !(durationSeconds > 0f) || float.IsInfinity(durationSeconds))
                {
                    Plugin.Log.LogWarning("Ectype: timed stat '" + key + "' not started: it needs a key, " +
                                          "a target, an amount function and a finite positive duration.");
                    return null;
                }
                if (!Plugin.PatchesApplied)
                {
                    // No driver means no update and no end: the modifier would stay forever.
                    if (WarnedOnce.Add("inert"))
                        Plugin.Log.LogWarning("Ectype: framework is inert this session; timed stat " +
                                              "contributions are disabled.");
                    return null;
                }

                FrameworkStats.EnsureRegistered();
                int id = StatRegistry.Resolve(statAlias, null);

                int now = PhotonNetwork.ServerTimestamp;
                int durationMs = (int)Math.Min(Math.Round(durationSeconds * 1000.0), int.MaxValue / 2);
                int elapsed = unchecked(now - startServerMs);
                if (elapsed < 0)
                {
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: timed stat '" + key + "' start stamp is " + (-elapsed) +
                                           " ms ahead of this machine's clock; starting now.");
                    startServerMs = now;
                    elapsed = 0;
                }

                TimedStatContribution old = Find(target, key);
                if (old != null) Finish(old, "replaced", true);

                if (elapsed >= durationMs)
                {
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: timed stat '" + key + "' not started: its " +
                                           durationSeconds.ToString("0.#") + " s window ended " +
                                           (elapsed - durationMs) + " ms ago on this machine's clock.");
                    return null;
                }

                c = new TimedStatContribution(key, label, target, id, startServerMs, durationMs,
                                              amountAt, keepAlive);
                c.Value = new CurveValue(c);
                // The amount is written by the value's Init inside ApplyModifiers.
                c.Mod = new FloatModifier(0f, type);
                StatMod mod = new StatMod(c.Mod, id) { DynamicValue = c.Value };
                target.ApplyModifiers(new List<StatMod> { mod }, c, true);
                Live.Add(c);

                if (!StatRegistry.CollectionHasStat(target, id) &&
                    WarnedOnce.Add("nomatch:" + key + ":" + id + ":" + HolderName(c)))
                    Plugin.Log.LogWarning("Ectype: timed stat '" + key + "' names stat " + statAlias +
                                          ", which " + HolderName(c) + "'s collection tree does not " +
                                          "register; it has no effect there.");

                string line = "Ectype: timed stat '" + c.Label + "' started on " + HolderName(c) + ": " +
                              statAlias + " " + type + " " + c.Amount.ToString("0.###") + " for " +
                              durationSeconds.ToString("0.#") + " s, " + elapsed + " ms in.";
                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Plugin.Log.LogInfo(line + " Further starts and ends log under [Debug] VerboseLogging.");
                }
                else if (Plugin.Verbose)
                {
                    Plugin.Log.LogInfo(line);
                }
                return c;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: starting timed stat '" + key + "' on " + statAlias +
                                    " failed: " + e);
                if (c != null)
                {
                    try { Finish(c, "start failed", true); }
                    catch (Exception e2) { Plugin.Log.LogError("Ectype: undoing timed stat '" + key + "' failed: " + e2); }
                }
                return null;
            }
        }

        /// <summary>End a contribution now and remove its modifier. No-op when it already ended.</summary>
        public static void End(TimedStatContribution contribution, string reason = "cancelled")
        {
            try
            {
                Finish(contribution, reason, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: ending timed stat '" +
                                    (contribution != null ? contribution.Key : "(null)") + "' failed: " + e);
            }
        }

        /// <summary>The live contribution with this key on this collection, or null.</summary>
        public static TimedStatContribution Find(StatTagCollection target, string key)
        {
            if (target == null || key == null) return null;
            for (int i = 0; i < Live.Count; i++)
                if (ReferenceEquals(Live[i].Target, target) && Live[i].Key == key) return Live[i];
            return null;
        }

        internal static float ProgressAt(int startMs, int durationMs, int nowMs)
        {
            if (durationMs <= 0) return 1f;
            int elapsed = unchecked(nowMs - startMs);
            if (elapsed <= 0) return 0f;
            if (elapsed >= durationMs) return 1f;
            return elapsed / (float)durationMs;
        }

        /// <summary>Driven from EctypeDriver.Update.</summary>
        internal static void Poll()
        {
            if (Live.Count == 0) return;
            float t = Time.unscaledTime;
            if (t < _nextUpdate) return;
            _nextUpdate = t + UpdateSeconds;

            int now = PhotonNetwork.ServerTimestamp;
            GameSession session = GameSessionManager.ActiveSession;
            // Backwards, so an end removing the current entry cannot skip the next one.
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (i >= Live.Count) continue;
                TimedStatContribution c = Live[i];
                try
                {
                    Update(c, now, session);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: updating timed stat '" + c.Key + "' failed; ending it: " + e);
                    try { Finish(c, "update failed", true); }
                    catch (Exception e2) { Plugin.Log.LogError("Ectype: ending timed stat '" + c.Key + "' failed: " + e2); }
                }
            }
        }

        private static void Update(TimedStatContribution c, int now, GameSession session)
        {
            // A destroyed holder took its collection with it; touching the tree would run the
            // change handlers of destroyed modules.
            if (c.HolderIsUnity && c.Holder == null) { Finish(c, "target destroyed", false); return; }
            if (!ReferenceEquals(session, c.Session)) { Finish(c, "session ended", true); return; }
            if (c.Faulted) { Finish(c, "amount function threw", true); return; }
            if (c.KeepAlive != null)
            {
                bool alive;
                try { alive = c.KeepAlive(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: timed stat '" + c.Key + "' keep-alive threw: " + e);
                    alive = false;
                }
                if (!alive) { Finish(c, "keep-alive returned false", true); return; }
            }
            if (unchecked(now - c.StartServerMs) >= c.DurationMs) { Finish(c, "expired", true); return; }
            c.Value.Refresh();
        }

        private static void Finish(TimedStatContribution c, string reason, bool removeFromTarget)
        {
            if (c == null || c.Ended) return;
            c.Ended = true;
            c.Reason = reason;
            Live.Remove(c);
            if (removeFromTarget && c.Target != null) c.Target.RemoveModifier(c);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: timed stat '" + c.Label + "' on " + HolderName(c) + " ended (" +
                                   reason + ") at " + (c.Progress * 100f).ToString("0") + "% of " +
                                   c.DurationSeconds.ToString("0.#") + " s.");
        }

        private static string HolderName(TimedStatContribution c)
        {
            if (c.HolderIsUnity) return c.Holder != null ? c.Holder.name : "(destroyed)";
            return c.Target != null && c.Target.Holder != null ? c.Target.Holder.GetType().Name : "(no holder)";
        }
    }

    /// <summary>
    /// The game's dynamic-value hook, driven by the contribution's curve. Description is overridden
    /// because the base reads a localisation table keyed by value type, which has no row for this
    /// one.
    /// </summary>
    internal sealed class CurveValue : ModDynamicValue<FloatModifier, float>
    {
        private readonly TimedStatContribution _owner;

        internal CurveValue(TimedStatContribution owner) { _owner = owner; }

        /// <summary>Rewrites the amount in place and tells every subscribed collection.</summary>
        internal void Refresh() { RecalculateValue(); }

        protected override float GetValue() { return _owner.AmountNow(); }

        protected override float DynamicValueBase
        {
            get { FloatModifier m = ModifierPrimitive as FloatModifier; return m != null ? m.Amount : 0f; }
        }

        protected override void OnInitialize() { }

        protected override void OnDestroy() { }

        public override string Description() { return _owner.Label; }
    }
}
