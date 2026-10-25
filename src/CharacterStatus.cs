using System;
using System.Collections.Generic;

using CG.Game.Player;
using Gameplay.Tags;
using Gameplay.Utilities;

using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Timed statuses (buffs/debuffs) on a player character - the counterpart to
    /// EnemyStatuses, which only targets CG.Space.OrbitObject. Not role-specific: a mod
    /// registers a template once at Awake, then applies it to any Player for a duration;
    /// the framework drives the timer and removes everything the status added on expiry.
    ///
    /// Built from the game's own machinery:
    ///  * Stat changes are StatMods applied to the character's StatTagCollection via
    ///    ApplyModifiers(mods, source, true) and removed with RemoveModifier(source), the
    ///    same pair a vanilla channel role ability uses on the same collection. Removal
    ///    is by source, so expiry cannot leak a modifier.
    ///  * Fresh StatMod instances per application: applying overwrites a modifier's
    ///    Source and removal matches on it, so one instance can never serve two targets
    ///    or two stacks.
    ///  * The character's stat surface is whatever Player.Awake registered, plus the
    ///    framework's own locomotion knobs (CharacterStats.Wire). A StatMod naming a stat
    ///    the character never registered is a silent no-op in game code, so this layer
    ///    logs one warning per (status, target type) when nothing matched.
    ///
    /// LOCAL ONLY, AND THAT IS CURRENTLY CORRECT FOR STATS. Every way a character takes
    /// damage resolves on that character's own client, so a vulnerability or resistance
    /// mod that exists only there is read by the code that matters:
    ///  * CustomCharacterHealth.CalculateRawDamage calls ApplyVulnerability in-process,
    ///    against the collection on the replica it runs on - there is no RPC inside
    ///    ApplyHitDamage, so authority is decided entirely by the call site.
    ///  * NPC damage abilities (cone, cylinder, AOE) gate their ApplyHitDamage on
    ///    Target.AmOwner - the victim's client.
    ///  * Environmental damage (void rifts, damage volumes, thruster wash, black holes,
    ///    oxygen and temperature attributes) reaches characters only through LocalPlayer.
    ///  * The one path that is not victim-side is a ship weapon's projectile impacting a
    ///    character, computed on the shooter's client; CalculateRawDamage zeroes
    ///    same-faction damage before vulnerability is read, so crew are unreachable
    ///    through it anyway.
    /// What is NOT covered by that argument is presentation and any callback whose effect
    /// other clients can see: OnApplied/OnTick/OnRemoved run only where Apply was called.
    /// A status whose payload is visible to the crew needs replication - see
    /// CharacterStatusReplication below for what that costs and what it must carry.
    ///
    /// Timing is Time.time, not the Photon clock EnemyStatuses uses. A local status has
    /// no peer to agree with, and the local clock is the one the ability durations and
    /// cooldowns beside it already run on. Replication changes that: see the stub.
    /// </summary>
    public sealed class CharacterStatusDef
    {
        /// <summary>Identity. Namespace it with the owning mod ("myrole.dash-guard").
        /// Replication, when it exists, will require the same literal on every client.</summary>
        public readonly string Key;

        /// <summary>Shown by GetHeader to anything that renders modifier sources.</summary>
        public string DisplayName;

        /// <summary>OnTick slice length in seconds. Ticks are indexed against the status's
        /// own start time, so a frame hitch pays every missed slice rather than losing
        /// them, and driving the poll twice in a frame cannot double-tick.</summary>
        public float TickSeconds = 1f;

        /// <summary>How many times Apply can compound on one character. Each stack applies
        /// the stat entries once more.</summary>
        public int MaxStacks = 1;

        /// <summary>Re-applying an active status re-arms its timer (true) or leaves the
        /// original expiry (false). Stacking applies either way, up to MaxStacks.</summary>
        public bool RefreshOnReapply = true;

        /// <summary>Runs when the status lands or gains a stack (target, stack count).</summary>
        public Action<Player, int> OnApplied;

        /// <summary>Runs once per elapsed tick (target, slice seconds). The place for
        /// bespoke per-tick work that is not a registered stat.</summary>
        public Action<Player, float> OnTick;

        /// <summary>Runs when the status ends, after its StatMods are removed. Target can
        /// be null if the character was destroyed first.</summary>
        public Action<Player> OnRemoved;

        /// <summary>Declares that this status must be visible on every client. NOT
        /// IMPLEMENTED: applying such a status still runs locally, and the framework logs
        /// one warning naming the key. Set it anyway on a status that needs it - the
        /// warning is how a mod finds out it is relying on behaviour that is not there
        /// yet, rather than shipping a buff that silently does nothing for the crew.</summary>
        public bool Replicated;

        internal readonly List<StatusStatEntry> Entries = new List<StatusStatEntry>();


        /// <summary>Starts a definition. The key identifies the status and must not be empty.</summary>
        public CharacterStatusDef(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is empty", "key");
            Key = key;
        }

        /// <summary>Add one stat modifier. Stat by alias - a vanilla StatType name
        /// ("KineticVulnerability"), a framework-owned stat ("ExternalForceDamping"), or a
        /// raw int id; modifier types as the manifest spells them: "percent" (+X of 1.0,
        /// multiplicative), "scalar" (xN), "flat", "flat2", or an exact ModifierType name.
        /// Amounts scale with Apply's magnitude.</summary>
        public CharacterStatusDef AddStat(string statAlias, string modifierType, float amount)
        {
            StatusStats.Add(Entries, statAlias, modifierType, amount, false);
            return this;
        }

        /// <summary>AddStat for an int-backed stat (vanilla ships no bool modifier; a
        /// conceptually-boolean stat is an int amount of 1).</summary>
        public CharacterStatusDef AddIntStat(string statAlias, string modifierType, int amount)
        {
            StatusStats.Add(Entries, statAlias, modifierType, amount, true);
            return this;
        }

        /// <summary>Every damage type at once, aggregate included. The aggregate alone is
        /// not enough: vanilla character perks modify the per-type stats, and the
        /// aggregate's read path on characters is unverified.</summary>
        public CharacterStatusDef AddAllVulnerabilities(string modifierType, float amount)
        {
            foreach (string alias in AllVulnerabilityAliases) AddStat(alias, modifierType, amount);
            return this;
        }

        internal static readonly string[] AllVulnerabilityAliases =
        {
            "Vulnerability", "KineticVulnerability", "EnergyVulnerability",
            "ElectricVulnerability", "FireVulnerability", "FreezingVulnerability",
            "PhysicalVulnerability", "RadiationVulnerability", "VoidVulnerability",
            "AsphyxiationVulnerability"
        };
    }

    /// <summary>
    /// The registry and the API. Register templates at plugin Awake; Apply/Remove from
    /// anywhere on the main thread. See CharacterStatusDef for the mechanism and the rules.
    /// </summary>
    public static class CharacterStatuses
    {
        private static readonly Dictionary<string, CharacterStatusDef> Defs =
            new Dictionary<string, CharacterStatusDef>();

        // One live status per (character instance id, key).
        private static readonly Dictionary<string, CharacterStatus> Active =
            new Dictionary<string, CharacterStatus>();
        private static readonly List<CharacterStatus> DriveScratch = new List<CharacterStatus>();
        private static readonly List<string> ReapScratch = new List<string>();

        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();

        /// <summary>Guarded and re-runnable: same key re-registers (last wins, named log)
        /// so a hot-reload cannot throw.</summary>
        public static void Register(CharacterStatusDef def)
        {
            if (def == null) { Plugin.Log.LogError("Ectype: Register(null character status)"); return; }
            if (Defs.ContainsKey(def.Key))
                Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' " +
                                      "re-registered; replacing the previous template.");
            Defs[def.Key] = def;
            Plugin.Log.LogInfo("Ectype: character status '" + def.Key + "' registered (" +
                               def.Entries.Count + " stat mods, max stacks " +
                               def.MaxStacks + ").");
        }

        /// <summary>
        /// Apply (or re-apply: refresh/stack per the template) a status to a character for
        /// a duration. Magnitude scales every stat amount. Local to this client - see the
        /// authority note on CharacterStatusDef.
        /// </summary>
        public static void Apply(Player target, string key, float seconds, float magnitude = 1f)
        {
            try
            {
                CharacterStatusDef def;
                if (!Preflight(target, key, out def)) return;
                if (seconds <= 0f)
                {
                    Plugin.Log.LogWarning("Ectype: Apply('" + key + "') with duration " +
                                          seconds + "s ignored.");
                    return;
                }
                if (def.Replicated) CharacterStatusReplication.WarnNotImplemented(def.Key);

                string mapKey = MapKey(target, def.Key);
                CharacterStatus live;
                if (Active.TryGetValue(mapKey, out live))
                {
                    live.Reapply(seconds);
                    return;
                }
                live = new CharacterStatus(def, target, mapKey, magnitude, seconds);
                Active[mapKey] = live;
                live.ApplyStack();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Apply('" + key + "') failed: " + e);
            }
        }

        /// <summary>Remove a status early. No-op when not active.</summary>
        public static void Remove(Player target, string key)
        {
            try
            {
                CharacterStatus live;
                if (target == null || !Active.TryGetValue(MapKey(target, key), out live)) return;
                live.End();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Remove('" + key + "') failed: " + e);
            }
        }

        /// <summary>Drop every status on a character. Call this when a character is about
        /// to stop being the thing the mods were meant for - a role switch, say. Death
        /// and despawn need no call: the poll reaps a status whose target went away.</summary>
        public static void RemoveAll(Player target)
        {
            if (target == null) return;
            ReapScratch.Clear();
            foreach (KeyValuePair<string, CharacterStatus> kv in Active)
                if (kv.Value.Targets(target)) ReapScratch.Add(kv.Key);
            for (int i = 0; i < ReapScratch.Count; i++)
            {
                CharacterStatus live;
                if (Active.TryGetValue(ReapScratch[i], out live)) live.End();
            }
        }

        /// <summary>True while the status with this key is active on the player.</summary>
        public static bool IsActive(Player target, string key)
        {
            return target != null && Active.ContainsKey(MapKey(target, key));
        }

        /// <summary>Seconds until expiry, 0 when not active.</summary>
        public static float RemainingSeconds(Player target, string key)
        {
            CharacterStatus live = Find(target, key);
            return live != null ? live.RemainingSeconds : 0f;
        }

        /// <summary>Current stack count of the status on the player, 0 when not active.</summary>
        public static int Stacks(Player target, string key)
        {
            CharacterStatus live = Find(target, key);
            return live != null ? live.StackCount : 0;
        }

        // ---------------------------------------------------------------- internals

        private static bool Preflight(Player target, string key, out CharacterStatusDef def)
        {
            def = null;
            if (target == null || target.Stats == null)
            {
                Plugin.Log.LogWarning("Ectype: character status '" + key + "' on a null " +
                                      "character (or one with no stat collection) ignored.");
                return false;
            }
            if (!Defs.TryGetValue(key ?? "", out def))
            {
                if (WarnedOnce.Add("nodef:" + key))
                    Plugin.Log.LogError("Ectype: character status '" + key + "' is not " +
                                        "registered. Register the template at Awake.");
                return false;
            }
            if (!Plugin.PatchesApplied)
            {
                // No driver means no timer: applying would be a permanent buff.
                if (WarnedOnce.Add("inert"))
                    Plugin.Log.LogWarning("Ectype: framework is inert this session; " +
                                          "character statuses are disabled.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Driven from EctypeDriver.Update. Expiry and ticks are both decided from the
        /// status's own start time, so this is safe to call more than once a frame.
        /// </summary>
        internal static void Poll()
        {
            if (Active.Count == 0) return;
            DriveScratch.Clear();
            foreach (KeyValuePair<string, CharacterStatus> kv in Active)
                DriveScratch.Add(kv.Value);
            for (int i = 0; i < DriveScratch.Count; i++) DriveScratch[i].Update();
        }

        internal static void Unregister(CharacterStatus live) { Active.Remove(live.MapKey); }

        internal static bool WarnOnce(string tag) { return WarnedOnce.Add(tag); }

        private static CharacterStatus Find(Player target, string key)
        {
            CharacterStatus live;
            return target != null && Active.TryGetValue(MapKey(target, key), out live)
                       ? live : null;
        }

        /// <summary>Keyed on the Unity instance id, not a view id: a status is local, and
        /// a respawned character is a different object that must not inherit the old
        /// one's entries.</summary>
        private static string MapKey(Player target, string key)
        {
            return target.GetInstanceID() + "/" + key;
        }
    }

    /// <summary>
    /// One live application. An IModifierSource, so it IS the removal key for every
    /// StatMod it applied.
    /// </summary>
    internal sealed class CharacterStatus : IDescriptiveModifierSource
    {
        internal readonly string MapKey;
        internal int StackCount { get { return _stacks; } }

        private readonly CharacterStatusDef _def;
        private readonly float _magnitude;
        private readonly float _startTime;
        private Player _target;
        private float _endTime;
        private int _stacks;
        private int _lastTick;
        private bool _ended;

        internal float RemainingSeconds
        {
            get { float r = _endTime - Time.time; return r > 0f ? r : 0f; }
        }

        internal CharacterStatus(CharacterStatusDef def, Player target, string mapKey,
                                 float magnitude, float seconds)
        {
            _def = def;
            _target = target;
            MapKey = mapKey;
            _magnitude = magnitude;
            _startTime = Time.time;
            _endTime = _startTime + seconds;
        }

        internal bool Targets(Player p) { return ReferenceEquals(_target, p); }

        internal void Reapply(float seconds)
        {
            if (_def.RefreshOnReapply) _endTime = Time.time + seconds;
            if (_stacks < _def.MaxStacks) ApplyStack();
            else if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_target, _stacks));
        }

        /// <summary>A character that went away (death, despawn, scene change) ends its
        /// statuses here rather than through any teardown hook: the target is a Unity
        /// object, so the null check catches a destroyed one, and the collection it held
        /// died with it.</summary>
        internal void Update()
        {
            try
            {
                if (_target == null) { End(); return; }
                if (Time.time >= _endTime) { End(); return; }
                Tick();
            }
            catch (Exception e)
            {
                if (CharacterStatuses.WarnOnce("tickfail:" + _def.Key))
                    Plugin.Log.LogError("Ectype: character status '" + _def.Key + "' tick " +
                                        "failed (suppressing further reports): " + e);
                End();
            }
        }

        internal void End()
        {
            if (_ended) return;
            _ended = true;
            Player t = _target;
            try
            {
                if (t != null && t.Stats != null) t.Stats.RemoveModifier(this);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: character status '" + _def.Key + "' stat " +
                                    "removal failed: " + e);
            }
            if (_def.OnRemoved != null) SafeCb(() => _def.OnRemoved(t));
            _target = null;
            CharacterStatuses.Unregister(this);
        }

        /// <summary>Fresh StatMod instances per application: the game keys removal on the
        /// modifier's Source field, which applying overwrites, so instances can never be
        /// shared between targets or stacks.</summary>
        internal void ApplyStack()
        {
            bool anyMatched;
            List<StatMod> mods = StatusStats.BuildMods(_def.Entries, _magnitude, _target.Stats,
                                                       "character status", out anyMatched);
            if (mods.Count > 0)
            {
                _target.Stats.ApplyModifiers(mods, this, true);
                if (!anyMatched &&
                    CharacterStatuses.WarnOnce("nomatch:" + _def.Key + ":" +
                                               _target.GetType().Name))
                    Plugin.Log.LogWarning("Ectype: character status '" + _def.Key +
                                          "' matched no registered stat on " +
                                          _target.GetType().Name + "; it has no effect there.");
            }
            _stacks++;
            if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_target, _stacks));
        }

        /// <summary>Tick-indexed against the status's own start: elapsed ticks are
        /// (now - start) / slice, and only unpaid indexes are paid, so double drives and
        /// frame hitches cannot double- or under-tick.</summary>
        private void Tick()
        {
            if (_def.OnTick == null) return;
            float slice = Math.Max(_def.TickSeconds, 0.05f);
            int due = (int)((Time.time - _startTime) / slice);
            while (_lastTick < due)
            {
                _lastTick++;
                Action<Player, float> cb = _def.OnTick;
                Player t = _target;
                SafeCb(() => cb(t, slice));
            }
        }


        private void SafeCb(Action a)
        {
            StatusStats.SafeCb(a, CharacterStatuses.WarnOnce, "character status", _def.Key);
        }

        // ------------------------------------------------- IDescriptiveModifierSource

        public string GetHeader()
        {
            return string.IsNullOrEmpty(_def.DisplayName) ? _def.Key : _def.DisplayName;
        }

        public string GetDescription()
        {
            return _def.Entries.Count + " stat mod(s)" + (_stacks > 1 ? ", x" + _stacks : "");
        }
    }

    /// <summary>
    /// The replication half, not built. What it would take, so the next person does not
    /// have to re-derive it:
    ///
    /// 1. Wire. A new EctypeNet subtype beside EnemyStatus (and a network contract bump),
    ///    carrying (op, target view id, key, start timestamp, duration ms, magnitude). Broadcast
    ///    with RuntimeAbilities.Recipients() as EnemyStatuses.Broadcast does. A Player is
    ///    a networked object, so PhotonView.Find resolves the target on the far side;
    ///    the local map key (Unity instance id) must become the view id in the same
    ///    change, or two clients will disagree about which status is which.
    /// 2. Clock. Time.time is per-process. A replicated status has to expire on
    ///    PhotonNetwork.ServerTimestamp, indexed the way EnemyStatusEffect does it, or
    ///    peers drift apart over a long duration.
    /// 3. Degradation. A peer without the template logs once and ignores the message
    ///    (EnemyStatuses already establishes this). That makes a replicated character
    ///    status honest only under MPType.All; a Session-typed mod gets divergent sims.
    ///    A framework-level status is also a room-content change: the applying mod
    ///    carries the session-flag posture.
    /// 4. Ownership. Any client may apply. Unlike NPC statuses there is a natural owner
    ///    here - the character's own client - so the alternative worth weighing first is
    ///    routing the request to the owner and letting it broadcast, which makes the
    ///    owner's sim the single authority for its own character.
    ///
    /// What it does NOT buy: the vulnerability/resistance case that motivated this layer.
    /// Character damage already resolves on the victim's client - see CharacterStatusDef.
    /// Build this when a status has a payload the crew must see (a visual, a callback
    /// with a world effect), not to make a defensive buff "more correct".
    /// </summary>
    internal static class CharacterStatusReplication
    {
        internal static void WarnNotImplemented(string key)
        {
            if (!CharacterStatuses.WarnOnce("norepl:" + key)) return;
            Plugin.Log.LogWarning("Ectype: character status '" + key + "' asks for " +
                                  "replication, which is not implemented. It runs on the " +
                                  "applying client only; callbacks and visuals are not shared.");
        }
    }
}
