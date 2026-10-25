using System;
using System.Collections.Generic;

using CG.Space;
using Gameplay.Utilities;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Timed statuses (buffs/debuffs) on space objects - NPC ships, turrets, asteroids,
    /// anything that is a CG.Space.OrbitObject. Not role-specific: a mod registers a
    /// status template once at Awake, then applies it to any target for a duration; the
    /// framework replicates the application to every peer, drives the timer on the game's
    /// own clock, and removes everything the status added when it expires.
    ///
    /// Built from the game's own machinery end to end (all IL-verified):
    ///  * The instance is a CG.Space.Effect subclass registered through
    ///    OrbitObject.ApplyEffect - the same object + list the stock jammer effect uses,
    ///    so vanilla bookkeeping (the per-object _effects list, its pump, debug spheres)
    ///    sees a modded status exactly like a stock one.
    ///  * Stat changes are StatMods applied to the target's StatTagCollection via
    ///    ApplyModifiers(mods, source, true) and removed with RemoveModifier(source) -
    ///    the exact calls Gameplay.NPC.AI.ActApplyStatMod makes when an NPC debuffs the
    ///    player ship, pointed the other way. Removal is by source, so expiry cannot leak
    ///    a modifier.
    ///  * Timing is Photon server timestamps (the clock CG.Space.Effect itself expires
    ///    on), so every client agrees on when a status ends regardless of frame rates.
    ///  * Damage over time goes through OrbitObject.ApplyRawDamage on the target's OWNER
    ///    client only (the master, for NPCs); HP replicates through the game's own
    ///    serialization, and set_HitPoints already calls Kill() at &lt;= 0 on the owner,
    ///    so a DoT can finish a target with no extra death handling.
    ///  * Replication rides the framework's own Photon event (EctypeNet), like AbilitySync:
    ///    the applier broadcasts (target view id, status key, timestamps, magnitude) and each
    ///    peer materializes the status locally against its own replica. Fresh modifier
    ///    instances are built per application - a shared StatMod instance cannot be
    ///    applied to two targets, because applying overwrites its Source and removal
    ///    matches by it.
    ///
    /// What a stat entry can actually hit: an OrbitObject's collection registers
    /// MaxHitPoints and Invulnerability, and SpaceCraft (every NPC ship) adds
    /// MaxFlightSpeed; level-scaled NPCs register their LevelStat set on top, and child
    /// collections (modules, turrets) are reached automatically because ApplyModifiers
    /// recurses into them. A StatMod naming a stat the target never registered is a
    /// silent no-op in the game's code, so this layer logs one warning per (status,
    /// target type) when nothing matched. Anything without a registered stat belongs in
    /// the OnTick/OnApplied callbacks instead.
    ///
    /// Authority and peers: any client may Apply - the stat mods land everywhere via the
    /// broadcast; only the target's owner ticks damage. The template must be registered
    /// under the same key on every client that should see it (register at plugin Awake
    /// from a literal); a peer without the key logs once and ignores the message, so a
    /// non-MPType.All mod degrades to "status invisible on that peer's sim" - fine for
    /// visuals-and-DoT, divergent for stat mods. Late joiners do not receive statuses
    /// already running. Anything applying these changes what a run contains: the
    /// applying mod carries the session-flag posture, as always.
    /// </summary>
    public sealed class EnemyStatusDef
    {
        /// <summary>Identity on the wire. Same literal on every client; namespace it with
        /// the owning mod ("mymod.burn").</summary>
        public readonly string Key;

        /// <summary>Shown by GetHeader to anything that renders modifier sources.</summary>
        public string DisplayName;

        /// <summary>Raw damage per second while active, dealt by the target's owner in
        /// TickSeconds slices through ApplyRawDamage (no damage-type table, no faction
        /// zeroing - the number you say is the number dealt). 0 = no DoT.</summary>
        public float DamagePerSecond;

        /// <summary>DoT/OnTick slice length in seconds. Ticks are indexed against the
        /// server clock, so driving the update twice in a frame cannot double-tick.</summary>
        public float TickSeconds = 1f;

        /// <summary>How many times Apply can compound on one target. Each stack applies
        /// the stat entries once more; DoT does not stack (magnitude does that).</summary>
        public int MaxStacks = 1;

        /// <summary>Re-applying an active status re-arms its timer (true) or leaves the
        /// original expiry (false). Stacking applies either way, up to MaxStacks.</summary>
        public bool RefreshOnReapply = true;

        /// <summary>Runs on every client when the status lands or gains a stack
        /// (target, stack count). Keep it cosmetic or idempotent.</summary>
        public Action<OrbitObject, int> OnApplied;

        /// <summary>Runs on the target's OWNER only, once per elapsed tick
        /// (target, attacker-or-null, slice seconds). The place for bespoke per-tick
        /// work that is not a registered stat.</summary>
        public Action<OrbitObject, OrbitObject, float> OnTick;

        /// <summary>Runs on every client when the status ends, after its StatMods are
        /// removed. Target can be null if it was destroyed first.</summary>
        public Action<OrbitObject> OnRemoved;

        /// <summary>While active, the target's abilities and its turrets' are suspended:
        /// casts hold, channelled and sustained effects are lifted, cooldowns stop and
        /// nothing new starts; all of it resumes where it stopped. Basic weapon fire and
        /// movement are untouched. Mechanism and coverage: <see cref="EnemyAbilities"/>.</summary>
        public bool SuspendsAbilities;

        /// <summary>Apply refuses a target that is invulnerable at that moment. Checked
        /// on the applying client only.</summary>
        public bool SkipsInvulnerableTargets;

        // ---- presentation (local-only cosmetics; see EnemyStatusFx.cs) ----

        /// <summary>Countdown marker on the affected object (the game's own
        /// reinforcement-call channel visual). On by default.</summary>
        public bool ShowMarker = true;

        /// <summary>Show the marker only while the player ship has a target lock on the
        /// affected object.</summary>
        public bool MarkerRequiresTargetLock;

        /// <summary>Marker label; defaults to DisplayName, then Key.</summary>
        public string MarkerText;

        /// <summary>Marker icon. Null uses the marker table's default Ability icon.
        /// EnemyStatuses.IconFromEmbeddedPng loads one from an embedded PNG.</summary>
        public Sprite Icon;

        /// <summary>Full-control visual: return a fresh GameObject and the framework
        /// parents it to the target (VfxLocalOffset/VfxScale applied) and destroys it
        /// when the status ends. Wins over VfxDonorName when both are set.</summary>
        public Func<OrbitObject, GameObject> CreateVfx;

        /// <summary>Stock-donor visual: substring name of a loaded prefab asset; cloned and
        /// stripped to visuals, attached for the status lifetime.</summary>
        public string VfxDonorName;

        /// <summary>Optional child-substring inside the donor (e.g. a GFX_ subtree).</summary>
        public string VfxDonorChild;

        /// <summary>Uniform scale applied to the attached visual.</summary>
        public float VfxScale = 1f;
        /// <summary>Offset of the attached visual from the target, in the target's local space.</summary>
        public Vector3 VfxLocalOffset = Vector3.zero;

        internal readonly List<StatusStatEntry> Entries = new List<StatusStatEntry>();


        /// <summary>Starts a definition. The key identifies the status and must not be empty.</summary>
        public EnemyStatusDef(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is empty", "key");
            Key = key;
        }

        /// <summary>Add one stat modifier. Stat by alias - a vanilla StatType name
        /// ("MaxFlightSpeed"), a framework-owned stat, or a raw int id; modifier types as
        /// the manifest spells them: "percent" (+X of 1.0, multiplicative), "scalar" (xN),
        /// "flat", "flat2", or an exact ModifierType name. Amounts scale with Apply's
        /// magnitude. So Slow-by-30%% is AddStat("MaxFlightSpeed", "percent", -0.3f).</summary>
        public EnemyStatusDef AddStat(string statAlias, string modifierType, float amount)
        {
            StatusStats.Add(Entries, statAlias, modifierType, amount, false);
            return this;
        }

        /// <summary>AddStat for an int-backed stat (vanilla ships no bool modifier; a
        /// conceptually-boolean stat like Invulnerability is an int amount of 1).</summary>
        public EnemyStatusDef AddIntStat(string statAlias, string modifierType, int amount)
        {
            StatusStats.Add(Entries, statAlias, modifierType, amount, true);
            return this;
        }
    }

    /// <summary>
    /// The registry and the API. Register templates at plugin Awake; Apply/Remove from
    /// anywhere on the main thread. See EnemyStatusDef for the mechanism and the rules.
    /// </summary>
    public static class EnemyStatuses
    {
        private static readonly Dictionary<string, EnemyStatusDef> Defs =
            new Dictionary<string, EnemyStatusDef>();

        // One live effect per (target view, key). Values are also reachable from the
        // target's own _effects list; this map is the driver's and the queries' index.
        private static readonly Dictionary<string, EnemyStatusEffect> Active =
            new Dictionary<string, EnemyStatusEffect>();
        private static readonly List<EnemyStatusEffect> DriveScratch =
            new List<EnemyStatusEffect>();

        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();

        /// <summary>Guarded and re-runnable: same key re-registers (last wins, named log)
        /// so a hot-reload cannot throw.</summary>
        public static void Register(EnemyStatusDef def)
        {
            if (def == null) { Plugin.Log.LogError("Ectype: Register(null status)"); return; }
            if (Defs.ContainsKey(def.Key))
                Plugin.Log.LogWarning("Ectype: status '" + def.Key + "' re-registered; " +
                                      "replacing the previous template.");
            Defs[def.Key] = def;
            Plugin.Log.LogInfo("Ectype: enemy status '" + def.Key + "' registered (" +
                               def.Entries.Count + " stat mods, dps " + def.DamagePerSecond +
                               ", max stacks " + def.MaxStacks + ").");
        }

        /// <summary>
        /// Apply (or re-apply: refresh/stack per the template) a status to a target for
        /// a duration, and broadcast the same application to every peer running the
        /// framework. Magnitude scales every stat amount and the DoT; attacker is
        /// optional and only feeds damage events and callbacks.
        /// </summary>
        public static void Apply(OrbitObject target, string key, float seconds,
                                 float magnitude = 1f, OrbitObject attacker = null)
        {
            try
            {
                EnemyStatusDef def;
                if (!Preflight(target, key, out def)) return;
                if (seconds <= 0f)
                {
                    Plugin.Log.LogWarning("Ectype: Apply('" + key + "') with duration " +
                                          seconds + "s ignored.");
                    return;
                }
                if (def.SkipsInvulnerableTargets && target.IsInvulnerable)
                {
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: Apply('" + key + "') skipped; " +
                                           target.name + " is invulnerable.");
                    return;
                }

                int startTs = PhotonNetwork.ServerTimestamp;
                int durationMs = (int)(seconds * 1000f);
                int attackerView = attacker != null && attacker.photonView != null
                                       ? attacker.photonView.ViewID : 0;
                ApplyLocal(target, def, startTs, durationMs, magnitude, attacker);
                Broadcast(new object[] { (byte)0, target.photonView.ViewID, key,
                                         startTs, durationMs, magnitude, attackerView });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Apply('" + key + "') failed: " + e);
            }
        }

        /// <summary>Remove a status early, everywhere. No-op when not active.</summary>
        public static void Remove(OrbitObject target, string key)
        {
            try
            {
                EnemyStatusDef def;
                if (!Preflight(target, key, out def)) return;
                RemoveLocal(target, key);
                Broadcast(new object[] { (byte)1, target.photonView.ViewID, key });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Remove('" + key + "') failed: " + e);
            }
        }

        /// <summary>True while the status with this key is active on the object.</summary>
        public static bool IsActive(OrbitObject target, string key)
        {
            return target != null && target.photonView != null &&
                   Active.ContainsKey(MapKey(target.photonView.ViewID, key));
        }

        /// <summary>Seconds until expiry, 0 when not active.</summary>
        public static float RemainingSeconds(OrbitObject target, string key)
        {
            EnemyStatusEffect fx = Find(target, key);
            return fx != null ? fx.RemainingSeconds : 0f;
        }

        /// <summary>Current stack count of the status on the object, 0 when not active.</summary>
        public static int Stacks(OrbitObject target, string key)
        {
            EnemyStatusEffect fx = Find(target, key);
            return fx != null ? fx.StackCount : 0;
        }

        // ---------------------------------------------------------------- internals

        private static bool Preflight(OrbitObject target, string key, out EnemyStatusDef def)
        {
            def = null;
            if (target == null || target.photonView == null)
            {
                Plugin.Log.LogWarning("Ectype: status '" + key + "' on a null/viewless " +
                                      "target ignored.");
                return false;
            }
            if (!Defs.TryGetValue(key ?? "", out def))
            {
                if (WarnedOnce.Add("nodef:" + key))
                    Plugin.Log.LogError("Ectype: status '" + key + "' is not registered " +
                                        "on this client. Register the template at Awake.");
                return false;
            }
            if (!Plugin.PatchesApplied)
            {
                // No driver means no timer: applying would be a permanent debuff.
                if (WarnedOnce.Add("inert"))
                    Plugin.Log.LogWarning("Ectype: framework is inert this session; enemy " +
                                          "statuses are disabled.");
                return false;
            }
            return true;
        }

        internal static void ApplyLocal(OrbitObject target, EnemyStatusDef def, int startTs,
                                        int durationMs, float magnitude, OrbitObject attacker)
        {
            string mapKey = MapKey(target.photonView.ViewID, def.Key);
            EnemyStatusEffect fx;
            if (Active.TryGetValue(mapKey, out fx))
            {
                fx.Reapply(startTs, durationMs);
                return;
            }
            fx = new EnemyStatusEffect(def, magnitude, attacker, mapKey, startTs, durationMs);
            Active[mapKey] = fx;
            // The game's own registration: joins the target's _effects list and calls
            // Activate -> our OnActivated applies the StatMods.
            target.ApplyEffect(attacker, fx, startTs);
        }

        internal static void RemoveLocal(OrbitObject target, string key)
        {
            if (target == null || target.photonView == null) return;
            EnemyStatusEffect fx;
            if (!Active.TryGetValue(MapKey(target.photonView.ViewID, key), out fx)) return;
            fx.Deactivate();                 // sets TimerExpired; OnDeactivated removes mods
            if (target != null) target.UpdateEffect(0f);   // vanilla prune of expired entries
        }

        /// <summary>
        /// Driven from EctypeDriver.Update, through the game's own Effect.Update - the same
        /// call the stock pump makes, so a vanilla EffectUpdater driving the same status this
        /// frame is a harmless no-op: every per-tick decision inside is server-clock indexed.
        /// </summary>
        internal static void Poll()
        {
            EnemyStatusMarkers.Poll(Active.Count > 0);
            if (Active.Count == 0) return;
            DriveScratch.Clear();
            foreach (KeyValuePair<string, EnemyStatusEffect> kv in Active)
                DriveScratch.Add(kv.Value);
            float dt = Time.deltaTime;
            for (int i = 0; i < DriveScratch.Count; i++)
            {
                EnemyStatusEffect fx = DriveScratch[i];
                fx.Update(dt);
                if (fx.TimerExpired) fx.PruneFromTarget();
            }
        }

        internal static void Unregister(EnemyStatusEffect fx)
        {
            Active.Remove(fx.MapKey);
        }

        internal static bool WarnOnce(string tag) { return WarnedOnce.Add(tag); }

        /// <summary>
        /// The shared presentation-cap gate. Both presentation layers degrade the same way
        /// past their cap - the status still runs, it just runs without that layer until a
        /// slot frees - so both report it identically and once. <paramref name="kind"/> names
        /// the layer ("marker"), <paramref name="omitted"/> what the status goes without
        /// ("a marker"), <paramref name="configKey"/> the key under [EnemyStatuses].
        /// </summary>
        internal static bool CapReached(int live, int cap, string kind, string omitted,
                                        string configKey)
        {
            if (live < Math.Max(0, cap)) return false;
            if (WarnOnce(kind + "cap"))
                Plugin.Log.LogWarning("Ectype: status-" + kind + " cap (" + cap + ") reached; " +
                                      "further statuses run without " + omitted + " until one " +
                                      "frees up ([EnemyStatuses] " + configKey + ").");
            return true;
        }

        internal static void CollectLive(List<EnemyStatusEffect> into)
        {
            foreach (KeyValuePair<string, EnemyStatusEffect> kv in Active)
                into.Add(kv.Value);
        }

        /// <summary>Load a status icon from a PNG embedded in the calling mod's assembly
        /// (the same loader role icons use; key matches by resource-name substring).</summary>
        public static Sprite IconFromEmbeddedPng(System.Reflection.Assembly asm, string key)
        {
            try { return IconStore.Get(asm, key); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: embedded status icon '" + key + "' failed: " +
                                      e.Message);
                return null;
            }
        }

        private static EnemyStatusEffect Find(OrbitObject target, string key)
        {
            if (target == null || target.photonView == null) return null;
            EnemyStatusEffect fx;
            return Active.TryGetValue(MapKey(target.photonView.ViewID, key), out fx) ? fx : null;
        }

        private static string MapKey(int viewId, string key) { return viewId + "/" + key; }

        private static void Broadcast(object[] args)
        {
            Player[] to = RuntimeAbilities.Recipients();
            if (to.Length == 0) return;
            EctypeNet.Send(EctypeNet.EnemyStatus, to, args, reliable: true);
        }

        internal static void HandleMessage(object[] args)
        {
            if (args == null || args.Length < 3) return;
            byte op = (byte)args[0];
            int viewId = (int)args[1];
            string key = args[2] as string;
            if (key == null) return;

            PhotonView view = PhotonView.Find(viewId);
            OrbitObject target = view != null ? view.GetComponent<OrbitObject>() : null;
            if (target == null) return;      // not instantiated here yet, or already gone

            if (op == 1) { RemoveLocal(target, key); return; }
            if (op != 0 || args.Length < 7) return;

            EnemyStatusDef def;
            if (!Defs.TryGetValue(key, out def))
            {
                // This peer lacks the registering mod. Documented degradation.
                if (WarnedOnce.Add("nodef:" + key))
                    Plugin.Log.LogWarning("Ectype: peer applied unknown enemy status '" +
                                          key + "'; ignoring (mod not installed here?).");
                return;
            }
            int startTs = (int)args[3];
            int durationMs = (int)args[4];
            float magnitude = (float)args[5];
            int attackerView = (int)args[6];
            PhotonView av = attackerView != 0 ? PhotonView.Find(attackerView) : null;
            OrbitObject attacker = av != null ? av.GetComponent<OrbitObject>() : null;
            ApplyLocal(target, def, startTs, durationMs, magnitude, attacker);
        }
    }

    /// <summary>
    /// One live application. A CG.Space.Effect, so the target's own effect list holds it and
    /// vanilla code sees it; an IModifierSource, so it IS the removal key for every StatMod it
    /// applied. Expiry is checked to the millisecond against the server clock in OnUpdate,
    /// with the base class's whole-second timer armed behind it as the vanilla-pump backstop.
    /// </summary>
    internal sealed class EnemyStatusEffect : Effect, IDescriptiveModifierSource
    {
        internal readonly string MapKey;
        internal int StackCount { get { return _stacks; } }

        private readonly EnemyStatusDef _def;
        private readonly float _magnitude;
        private readonly OrbitObject _attacker;
        private readonly int _activationTs;     // server ts handed to ApplyEffect
        private int _endTs;                     // server ts of precise expiry
        private OrbitObject _tgt;
        private int _stacks;
        private long _lastTick;
        private bool _removed;

        internal float RemainingSeconds
        {
            get
            {
                int ms = unchecked(_endTs - PhotonNetwork.ServerTimestamp);
                return ms > 0 ? ms / 1000f : 0f;
            }
        }

        internal EnemyStatusEffect(EnemyStatusDef def, float magnitude, OrbitObject attacker,
                                   string mapKey, int startTs, int durationMs)
        {
            _def = def;
            _magnitude = magnitude;
            _attacker = attacker;
            MapKey = mapKey;
            _activationTs = startTs;
            _endTs = unchecked(startTs + durationMs);
            ArmBaseTimer();
        }

        /// <summary>Base Effect expires at startTime + Duration*1000 (whole seconds). Keep
        /// that two seconds behind the precise expiry so whichever pump runs first, the
        /// millisecond check in OnUpdate decides - and cap it, because Duration*1000 is
        /// int math in game code.</summary>
        private void ArmBaseTimer()
        {
            long secs = unchecked(_endTs - _activationTs) / 1000L + 2L;
            Duration = (int)Math.Min(Math.Max(secs, 1L), 2000000L);
        }

        internal void Reapply(int startTs, int durationMs)
        {
            if (_def.RefreshOnReapply)
            {
                _endTs = unchecked(startTs + durationMs);
                ArmBaseTimer();
            }
            if (_stacks < _def.MaxStacks) ApplyStack();
            else if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_tgt, _stacks));
            Presentation(true);   // remove + re-add restarts the countdown circle
        }

        internal void PruneFromTarget()
        {
            EnemyStatuses.Unregister(this);
            if (_tgt != null) _tgt.UpdateEffect(0f);   // vanilla removal from _effects
        }

        // ------------------------------------------------------------ Effect hooks

        internal EnemyStatusDef Def { get { return _def; } }
        internal OrbitObject Target { get { return _tgt; } }

        /// <summary>Server timestamp of the precise expiry; moves on a refresh.</summary>
        internal int EndTs { get { return _endTs; } }

        internal long RemainingMs
        {
            get
            {
                int ms = unchecked(_endTs - PhotonNetwork.ServerTimestamp);
                return ms > 0 ? ms : 0;
            }
        }

        protected override void OnActivated(OrbitObject target)
        {
            _tgt = target;
            ApplyStack();
            // Anchored on the status's own start, which every client shares, so a client
            // the status reached late freezes the same span as the one that applied it.
            if (_def.SuspendsAbilities) EnemyAbilities.Begin(this, target, _activationTs);
            Presentation(true);
        }

        /// <summary>Presentation must never break the sim: marker and vfx failures log
        /// once and leave the status itself running.</summary>
        private void Presentation(bool show)
        {
            try
            {
                if (show)
                {
                    EnemyStatusMarkers.Show(this);
                    EnemyStatusVfx.Attach(this);
                }
                else
                {
                    EnemyStatusMarkers.Hide(this);
                    EnemyStatusVfx.Detach(this);
                }
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("fx:" + _def.Key))
                    Plugin.Log.LogError("Ectype: status '" + _def.Key + "' presentation " +
                                        "failed (suppressing further reports): " + e);
            }
        }

        protected override void OnUpdate(OrbitObject target, float deltaTime)
        {
            try
            {
                if (unchecked(PhotonNetwork.ServerTimestamp - _endTs) >= 0)
                {
                    Deactivate();
                    return;
                }
                TickDamage(target);
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("tickfail:" + _def.Key))
                    Plugin.Log.LogError("Ectype: status '" + _def.Key + "' tick failed " +
                                        "(suppressing further reports): " + e);
            }
        }

        /// <summary>Runs twice at end of life by design in the game's code (the expiry
        /// path calls it, then the prune pass calls it again, the second time with a null
        /// target) - hence the guard.</summary>
        protected override void OnDeactivated(OrbitObject target)
        {
            if (_removed) return;
            _removed = true;
            OrbitObject t = _tgt != null ? _tgt : target;
            try
            {
                if (t != null) t.Stats.RemoveModifier(this);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: status '" + _def.Key + "' stat removal " +
                                    "failed: " + e);
            }
            if (_def.SuspendsAbilities)
            {
                // The natural expiry when it has passed, so every client resumes from the
                // same point; an early removal ends now.
                int now = PhotonNetwork.ServerTimestamp;
                EnemyAbilities.End(this, unchecked(now - _endTs) >= 0 ? _endTs : now);
            }
            if (_def.OnRemoved != null) SafeCb(() => _def.OnRemoved(t));
            Presentation(false);
            EnemyStatuses.Unregister(this);
        }

        // ------------------------------------------------------------ the work

        /// <summary>Fresh StatMod instances per application: the game keys removal on the
        /// modifier's Source field, which applying overwrites, so instances can never be
        /// shared between targets or stacks.</summary>
        private void ApplyStack()
        {
            bool anyMatched;
            List<StatMod> mods = StatusStats.BuildMods(_def.Entries, _magnitude, _tgt.Stats,
                                                       "enemy status", out anyMatched);
            if (mods.Count > 0)
            {
                // Same call, same bool, as the game's own NPC debuff apply.
                _tgt.Stats.ApplyModifiers(mods, this, true);
                if (!anyMatched &&
                    EnemyStatuses.WarnOnce("nomatch:" + _def.Key + ":" + _tgt.GetType().Name))
                    Plugin.Log.LogWarning("Ectype: status '" + _def.Key + "' matched no " +
                                          "registered stat on " + _tgt.GetType().Name +
                                          "; it has no effect there.");
            }
            _stacks++;
            if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_tgt, _stacks));
        }

        /// <summary>Owner-only, tick-indexed against the server clock: elapsed ticks are
        /// (now - activation) / slice, and only unpaid indexes are paid, so double drives
        /// and frame hitches cannot double- or under-tick.</summary>
        private void TickDamage(OrbitObject target)
        {
            if (_def.DamagePerSecond <= 0f && _def.OnTick == null) return;
            if (target == null || !target.AmOwner) return;
            int tickMs = (int)(Math.Max(_def.TickSeconds, 0.05f) * 1000f);
            long due = (long)unchecked(PhotonNetwork.ServerTimestamp - _activationTs) / tickMs;
            while (_lastTick < due)
            {
                _lastTick++;
                if (target.HitPoints <= 0f) break;
                if (_def.DamagePerSecond > 0f)
                    target.ApplyRawDamage(
                        _def.DamagePerSecond * _magnitude * (tickMs / 1000f), _attacker);
                if (_def.OnTick != null)
                {
                    Action<OrbitObject, OrbitObject, float> cb = _def.OnTick;
                    SafeCb(() => cb(target, _attacker, tickMs / 1000f));
                }
            }
        }


        private void SafeCb(Action a)
        {
            StatusStats.SafeCb(a, EnemyStatuses.WarnOnce, "status", _def.Key);
        }

        // ------------------------------------------------- IDescriptiveModifierSource

        public string GetHeader()
        {
            return string.IsNullOrEmpty(_def.DisplayName) ? _def.Key : _def.DisplayName;
        }

        public string GetDescription()
        {
            return _def.Entries.Count + " stat mod(s)" +
                   (_def.DamagePerSecond > 0f
                        ? ", " + (_def.DamagePerSecond * _magnitude) + "/s" : "") +
                   (_stacks > 1 ? ", x" + _stacks : "");
        }
    }

    /// <summary>
    /// Receiver, registered with EctypeNet as subtype <see cref="EctypeNet.EnemyStatus"/>.
    /// Deliberately not owner-gated: any crew member's client may apply a status to an
    /// NPC (the applier is not the view owner - the master is), and the payload can only
    /// reference templates this client itself registered.
    /// </summary>
    internal static class EnemyStatusMessage
    {
        internal static void Receive(int senderActor, object[] args)
        {
            try
            {
                EnemyStatuses.HandleMessage(args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling enemy-status message failed: " + e);
            }
        }
    }
}
