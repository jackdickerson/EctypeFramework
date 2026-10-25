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
    /// broadcast; only the target's owner ticks damage. Every receiver judges the sender of
    /// an apply against the template's Stance and drops one that fails it, and accepts a
    /// Remove from the master and from any sender the Stance would let apply the status;
    /// the sending client runs the same checks first, so a refused change happens nowhere.
    /// Stance MasterOnly accepts the master client alone, for both. A status can also target a
    /// part of a networked object (StatusPart, EnemyStatusParts.cs): same timer, stacking and
    /// authority, no stats or presentation. The template must be registered
    /// under the same key on every client that should see it (register at plugin Awake
    /// from a literal); a peer without the key logs once and ignores the message, so a
    /// non-MPType.All mod degrades to "status invisible on that peer's sim" - fine for
    /// visuals-and-DoT, divergent for stat mods. Late joiners do not receive statuses
    /// already running. Anything applying these changes what a run contains: the
    /// applying mod carries the session-flag posture, as always.
    /// </summary>
    public sealed class EnemyStatusDef
    {
        /// <summary>Identity on the wire: <c>"&lt;plugin GUID&gt;/&lt;name&gt;"</c>, where the GUID is
        /// the registering plugin's BepInEx GUID. Same literal on every client.</summary>
        public readonly string Key;

        /// <summary>Shown by GetHeader to anything that renders modifier sources.</summary>
        public string DisplayName;

        /// <summary>Raw damage per second while active, dealt by the target's owner in
        /// TickSeconds slices through ApplyRawDamage (no damage-type table, no faction
        /// zeroing - the number you say is the number dealt). 0 = no DoT. Pair it with
        /// Stance Hostile: the owner then also skips each tick while the applier is no
        /// longer on another side from the target.</summary>
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

        /// <summary>Runs on every client when the status lands or gains a stack, on a space
        /// object or on a part, after OnApplied: the instance with its target or part, its Tag,
        /// its stacks and whether this application created it. The place to add what the status
        /// enforces, such as a change validator on a part.</summary>
        public Action<StatusInstance> OnInstanceApplied;

        /// <summary>Runs on every client when the status ends, on a space object or on a part,
        /// after OnRemoved: the instance as it ended, with its Tag. The place to take away what
        /// OnInstanceApplied added.</summary>
        public Action<StatusInstance> OnInstanceRemoved;

        /// <summary>While active, the target's abilities and its turrets' are suspended:
        /// casts hold, channelled and sustained effects are lifted, cooldowns stop and
        /// nothing new starts; all of it resumes where it stopped. Basic weapon fire and
        /// movement are untouched. Mechanism and coverage: <see cref="EnemyAbilities"/>.</summary>
        public bool SuspendsAbilities;

        /// <summary>Apply refuses a target that is invulnerable at that moment. Checked
        /// on the applying client only.</summary>
        public bool SkipsInvulnerableTargets;

        /// <summary>Which sender-to-target relation the status accepts, for an application
        /// and for a removal. The sender is the client that sends it, judged by its
        /// character's side (its crew ship's when it has no character). The attacker argument
        /// counts in one case only: the master applying with an NPC attacker is judged by
        /// that NPC's side. Any (the default) accepts every sender; Hostile only a sender on
        /// another side from the target; Friendly only one on the target's side. The master
        /// may always remove. MasterOnly accepts only the master client, for applications and
        /// removals alike. Every receiver judges its copy of the message, and a status with
        /// damage over time should be Hostile.</summary>
        public StatusStance Stance = StatusStance.Any;

        /// <summary>Milliseconds over which this client's applications of the status to one
        /// target travel as one message. 0 (the default) sends each application at once.
        /// Above 0, Apply still takes effect on this client at once, so a later hit in the
        /// same frame sees the earlier stacks; the peers receive one message per target per
        /// window carrying the number of applications, and add that many, so applications
        /// from several clients add up. About 150 suits a status applied per projectile hit.</summary>
        public int CoalesceMs;

        // ---- presentation (local-only cosmetics; see EnemyStatusFx.cs) ----

        /// <summary>Countdown marker on the affected object (the game's own
        /// reinforcement-call channel visual). On by default.</summary>
        public bool ShowMarker = true;

        /// <summary>Show the marker only while the player ship has a target lock on the
        /// affected object.</summary>
        public bool MarkerRequiresTargetLock;

        /// <summary>Which viewers draw the marker and the visual: All (the default), OwnForce
        /// (players on the applier's side), TargetForce (players on the target's side) or
        /// HostileToTarget (players on another side from the target). Presentation only: the
        /// status, its stat changes and its callbacks run on every client, and IsActive
        /// answers on every client, so this is not secrecy against a modified client.</summary>
        public StatusVisibility Visibility = StatusVisibility.All;

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
    public static partial class EnemyStatuses
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
            Identifiers.WarnId("enemy status", def.Key);
            if (Defs.ContainsKey(def.Key))
                Plugin.Log.LogWarning("Ectype: status '" + def.Key + "' re-registered; " +
                                      "replacing the previous template.");
            Defs[def.Key] = def;
            Plugin.Log.LogInfo("Ectype: enemy status '" + def.Key + "' registered (" +
                               def.Entries.Count + " stat mods, dps " + def.DamagePerSecond +
                               ", max stacks " + def.MaxStacks + ").");
            if (def.DamagePerSecond > 0f && def.Stance == StatusStance.Any)
                Plugin.Log.LogWarning("Ectype: status '" + def.Key + "' deals damage over time " +
                                      "with Stance Any, so it also damages a target on the " +
                                      "applier's own side; set Stance = StatusStance.Hostile.");
        }

        /// <summary>
        /// Apply (or re-apply: refresh/stack per the template) a status to a target for
        /// a duration, and broadcast the same application to every peer running the
        /// framework. Magnitude scales every stat amount and the DoT; attacker is
        /// optional and feeds damage events and callbacks; on the master it also stands in
        /// for the sender's side when it is an NPC (see EnemyStatusDef.Stance). Fire and
        /// forget: a refusal for the stance is logged once per key, and
        /// <see cref="TryApply(OrbitObject, string, float, float, OrbitObject)"/> returns why nothing happened.
        /// </summary>
        public static void Apply(OrbitObject target, string key, float seconds,
                                 float magnitude = 1f, OrbitObject attacker = null)
        {
            ApplyCore(target, key, seconds, magnitude, attacker, 0, true);
        }

        /// <summary>
        /// <see cref="Apply(OrbitObject, string, float, float, OrbitObject)"/> carrying a
        /// <paramref name="tag"/>: a number every client reads back through
        /// <see cref="Tag(OrbitObject, string)"/> and <see cref="StatusInstance.Tag"/>, such as
        /// the team a window is for. A re-application replaces the Tag with its own.
        /// </summary>
        public static void Apply(OrbitObject target, string key, float seconds, float magnitude,
                                 OrbitObject attacker, int tag)
        {
            ApplyCore(target, key, seconds, magnitude, attacker, tag, true);
        }

        /// <summary>
        /// <see cref="Apply(OrbitObject, string, float, float, OrbitObject)"/>, returning
        /// <see cref="StatusRefusal.None"/> when the status was applied here and its application
        /// sent to the peers (or queued for them, when the template sets CoalesceMs), else the
        /// reason it was not. The stance check runs here first with this client as the sender,
        /// the same check every receiver runs, so a refused application is applied nowhere.
        /// </summary>
        public static StatusRefusal TryApply(OrbitObject target, string key, float seconds,
                                             float magnitude = 1f, OrbitObject attacker = null)
        {
            return ApplyCore(target, key, seconds, magnitude, attacker, 0, false);
        }

        /// <summary><see cref="TryApply(OrbitObject, string, float, float, OrbitObject)"/>
        /// carrying a <paramref name="tag"/>, as the tagged Apply does.</summary>
        public static StatusRefusal TryApply(OrbitObject target, string key, float seconds,
                                             float magnitude, OrbitObject attacker, int tag)
        {
            return ApplyCore(target, key, seconds, magnitude, attacker, tag, false);
        }

        private static StatusRefusal ApplyCore(OrbitObject target, string key, float seconds,
                                               float magnitude, OrbitObject attacker, int tag,
                                               bool fireAndForget)
        {
            try
            {
                EnemyStatusDef def;
                StatusRefusal pre = Preflight(target, key, out def);
                if (pre != StatusRefusal.None) return pre;
                if (!(seconds > 0f) || float.IsInfinity(seconds))
                {
                    Plugin.Log.LogWarning("Ectype: Apply('" + key + "') with duration " +
                                          seconds + "s ignored.");
                    return StatusRefusal.InvalidDuration;
                }
                if (def.SkipsInvulnerableTargets && target.IsInvulnerable)
                {
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: Apply('" + key + "') skipped; " +
                                           target.name + " is invulnerable.");
                    return StatusRefusal.Invulnerable;
                }

                int me = LocalActor();
                int myForce;
                bool byAttacker;
                StatusSide side = SenderSide(me, target, attacker, out myForce, out byAttacker);
                StatusRefusal stance = ApplicationVerdict(def.Stance, IsMaster(me), side);
                if (stance != StatusRefusal.None)
                {
                    ReportOwnRefusal("Apply", key, target.name, Reason(stance, def, side, me), fireAndForget);
                    return stance;
                }

                int startTs = PhotonNetwork.ServerTimestamp;
                int durationMs = (int)(seconds * 1000f);
                int attackerView = attacker != null && attacker.photonView != null
                                       ? attacker.photonView.ViewID : 0;
                // The batch leaves on the first frame after the window and still has to travel;
                // a receiver that never had the status ignores one that arrives after its end.
                if (def.CoalesceMs > 0 && durationMs <= def.CoalesceMs + ShortDurationMarginMs &&
                    WarnedOnce.Add("coalesce-short:" + key))
                    Plugin.Log.LogWarning("Ectype: status '" + key + "' applied for " + durationMs +
                                          " ms with CoalesceMs " + def.CoalesceMs + "; it can end " +
                                          "before its batch arrives, and the peers then never see it.");
                bool created;
                EnemyStatusEffect fx = ApplyLocal(target, def, startTs, durationMs, magnitude, attacker,
                                                  me, myForce, byAttacker, tag, 1, out created);
                if (def.CoalesceMs > 0)
                {
                    int view = target.photonView.ViewID;
                    // A batch belongs to one life of the local status, so a new life closes
                    // whatever is still open for the old one.
                    if (created) Outbox.Close(view, key);
                    Outbox.Add(view, key, target.name, fx.ActivationTs, fx.EndTs, magnitude,
                               attackerView, def.CoalesceMs, Time.unscaledTime, tag);
                }
                else
                    Broadcast(ApplyArgs(target.photonView.ViewID, key, startTs, durationMs, magnitude,
                                        attackerView, tag));
                return StatusRefusal.None;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Apply('" + key + "') failed: " + e);
                return StatusRefusal.Failed;
            }
        }

        /// <summary>Remove a status early, everywhere. No-op when not active. Accepted from a
        /// client the template's Stance would let apply it, and from the master; a refusal is
        /// logged once per key, and <see cref="TryRemove(OrbitObject, string)"/> returns it.</summary>
        public static void Remove(OrbitObject target, string key)
        {
            RemoveCore(target, key, true);
        }

        /// <summary>
        /// <see cref="Remove(OrbitObject, string)"/>, returning why nothing was sent. A removal is accepted from a
        /// sender the template's Stance would let apply the status (anyone for Any) and from
        /// the master; anything else is <see cref="StatusRefusal.Authority"/>, judged here
        /// first because every receiver holds the same rule and would ignore the removal.
        /// </summary>
        public static StatusRefusal TryRemove(OrbitObject target, string key)
        {
            return RemoveCore(target, key, false);
        }

        private static StatusRefusal RemoveCore(OrbitObject target, string key, bool fireAndForget)
        {
            try
            {
                EnemyStatusDef def;
                StatusRefusal pre = Preflight(target, key, out def);
                if (pre != StatusRefusal.None) return pre;
                StatusSide side;
                int me = LocalActor();
                StatusRefusal verdict = JudgeRemoval(def, me, target, out side);
                if (verdict != StatusRefusal.None)
                {
                    ReportOwnRefusal("Remove", key, target.name, Reason(verdict, def, side, me), fireAndForget);
                    return verdict;
                }
                int viewId = target.photonView.ViewID;
                int removedTs = PhotonNetwork.ServerTimestamp;
                // Applications still queued would re-add the status on the peers after the removal.
                Outbox.Drop(viewId, key);
                // Sent and remembered before the local removal runs its callbacks: an OnRemoved or
                // OnInstanceRemoved that applies the same key again queues or sends that
                // application after the removal, starting at or after removedTs, so every receiver
                // keeps the new life.
                RecordRemoval(viewId, def, removedTs);
                Broadcast(new object[] { (byte)1, viewId, key, removedTs });
                RemoveLocal(target, key);
                return StatusRefusal.None;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Remove('" + key + "') failed: " + e);
                return StatusRefusal.Failed;
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

        /// <summary>The Tag the latest accepted application carried, 0 when not active or
        /// applied without one.</summary>
        public static int Tag(OrbitObject target, string key)
        {
            EnemyStatusEffect fx = Find(target, key);
            return fx != null ? fx.Tag : 0;
        }

        // ---------------------------------------------------------------- internals

        private static StatusRefusal Preflight(OrbitObject target, string key, out EnemyStatusDef def)
        {
            def = null;
            if (target == null || target.photonView == null)
            {
                Plugin.Log.LogWarning("Ectype: status '" + key + "' on a null/viewless " +
                                      "target ignored.");
                return StatusRefusal.InvalidTarget;
            }
            if (!Defs.TryGetValue(key ?? "", out def))
            {
                if (WarnedOnce.Add("nodef:" + key))
                    Plugin.Log.LogError("Ectype: status '" + key + "' is not registered " +
                                        "on this client. Register the template at Awake.");
                return StatusRefusal.NotRegistered;
            }
            if (!Plugin.PatchesApplied)
            {
                // No driver means no timer: applying would be a permanent debuff.
                if (WarnedOnce.Add("inert"))
                    Plugin.Log.LogWarning("Ectype: framework is inert this session; enemy " +
                                          "statuses are disabled.");
                return StatusRefusal.Inert;
            }
            return StatusRefusal.None;
        }

        /// <summary>Applies one application, or <paramref name="stacks"/> applications at once
        /// with the timer moved once, on this client, and returns the live status. The creating
        /// application's actor and side are kept for logging and the damage-over-time recheck;
        /// every accepted applier's side is added to the set the OwnForce visibility rule
        /// reads, so that set is the same on every client whatever order applications arrive
        /// in.</summary>
        internal static EnemyStatusEffect ApplyLocal(OrbitObject target, EnemyStatusDef def, int startTs,
                                                     int durationMs, float magnitude, OrbitObject attacker,
                                                     int applierActor, int applierForce, bool byAttacker,
                                                     int tag, int stacks, out bool created)
        {
            if (stacks < 1) stacks = 1;
            int viewId = target.photonView.ViewID;
            string mapKey = MapKey(viewId, def.Key);
            EnemyStatusEffect fx;
            if (Active.TryGetValue(mapKey, out fx))
            {
                created = false;
                fx.NoteApplierSide(applierForce);
                fx.Reapply(startTs, durationMs, stacks, tag);
                return fx;
            }
            created = true;
            fx = new EnemyStatusEffect(def, magnitude, attacker, viewId, mapKey, startTs, durationMs,
                                       applierActor, applierForce, byAttacker, tag);
            Active[mapKey] = fx;
            // The game's own registration: joins the target's _effects list and calls
            // Activate -> our OnActivated applies the StatMods (the first stack).
            target.ApplyEffect(attacker, fx, startTs);
            if (stacks > 1) fx.AddStacks(stacks - 1);
            return fx;
        }

        // ---------------------------------------------------------------- authority

        /// <summary>This client's actor number; 0 before PUN has a local player.</summary>
        internal static int LocalActor()
        {
            Player me = PhotonNetwork.LocalPlayer;
            return me != null ? me.ActorNumber : 0;
        }

        private static Player PlayerOf(int actor)
        {
            Room room = PhotonNetwork.CurrentRoom;
            if (room != null) return room.GetPlayer(actor);
            Player me = PhotonNetwork.LocalPlayer;
            return me != null && me.ActorNumber == actor ? me : null;
        }

        /// <summary>
        /// Which side of the target an actor is on. With a character, the game's own
        /// different-faction check between the character and the target (Topology.Hostile
        /// over two space objects). Without one (a spectator, a player between death and
        /// respawn), the side Topology gives the player: the faction of their crew ship, or
        /// of a living ship of their team under Void Crew Fleet. Unknown when neither exists
        /// or the actor is not in the room. <paramref name="force"/> is the actor's faction,
        /// -1 when unknown.
        /// </summary>
        internal static StatusSide SideOf(int actor, OrbitObject target, out int force)
        {
            force = -1;
            if (target == null) return StatusSide.Unknown;
            CG.Game.Player.Player character = Topology.CharacterOf(actor);
            if (character != null)
            {
                force = character.Faction;
                return Topology.Hostile(character, target) ? StatusSide.Other : StatusSide.Same;
            }
            Player player = PlayerOf(actor);
            if (player == null) return StatusSide.Unknown;
            force = Topology.ForceOf(player);
            if (force < 0) return StatusSide.Unknown;
            return Topology.Hostile(player, target) ? StatusSide.Other : StatusSide.Same;
        }

        /// <summary>
        /// The sender's side for an application. One case judges by the attacker instead: the
        /// sender is the master and the attacker is an NPC (a space object that is neither a
        /// player ship, a part of one, nor a character). NPCs are simulated on the master, so
        /// only the master applies a status on an NPC's behalf, and the master is trusted with
        /// any removal already. Every other attacker argument is ignored here, because any
        /// client could fill it in. <paramref name="byAttacker"/> says which case applied.
        /// </summary>
        internal static StatusSide SenderSide(int actor, OrbitObject target, OrbitObject attacker,
                                              out int force, out bool byAttacker)
        {
            byAttacker = false;
            if (target != null && attacker != null && IsMaster(actor) && IsNpc(attacker))
            {
                byAttacker = true;
                force = attacker.Faction;
                return Topology.Hostile(attacker, target) ? StatusSide.Other : StatusSide.Same;
            }
            return SideOf(actor, target, out force);
        }

        private static bool IsNpc(OrbitObject o)
        {
            if (o is PlayerControlledShip || o is CG.Game.Player.Player) return false;
            return Topology.ShipOfPart(o) == null;
        }

        internal static bool IsMaster(int actor)
        {
            if (actor == LocalActor() && PhotonNetwork.IsMasterClient) return true;
            Player master = PhotonNetwork.MasterClient;
            return master != null && master.ActorNumber == actor;
        }

        /// <summary>The application rule as a table over its inputs: MasterOnly by who sends,
        /// every other stance by the sender's side. Pure, for the offline self-test.</summary>
        internal static StatusRefusal ApplicationVerdict(StatusStance stance, bool senderIsMaster, StatusSide side)
        {
            if (stance == StatusStance.MasterOnly)
                return senderIsMaster ? StatusRefusal.None : StatusRefusal.NotMaster;
            if (stance == StatusStance.Any) return StatusRefusal.None;
            if (side == StatusSide.Unknown) return StatusRefusal.UnknownSide;
            if (stance == StatusStance.Hostile) return side == StatusSide.Other ? StatusRefusal.None : StatusRefusal.Stance;
            if (stance == StatusStance.Friendly) return side == StatusSide.Same ? StatusRefusal.None : StatusRefusal.Stance;
            // A stance this build does not know accepts nothing.
            return StatusRefusal.Stance;
        }

        /// <summary>The removal rule as a table over its inputs, for the offline self-test.</summary>
        internal static StatusRefusal RemovalVerdict(StatusStance stance, bool senderIsMaster, StatusSide side)
        {
            if (stance == StatusStance.MasterOnly)
                return senderIsMaster ? StatusRefusal.None : StatusRefusal.NotMaster;
            if (stance == StatusStance.Any || senderIsMaster) return StatusRefusal.None;
            return ApplicationVerdict(stance, false, side) == StatusRefusal.None
                       ? StatusRefusal.None : StatusRefusal.Authority;
        }

        /// <summary>
        /// Who may remove a status, the same answer on every client and in any order: the
        /// master, and any sender the template's Stance would let apply it (so anyone for
        /// Any). Stateless, so two clients that applied concurrently can each remove it, and a
        /// player who rejoins under a new actor number keeps the right their side gives them.
        /// </summary>
        internal static StatusRefusal JudgeRemoval(EnemyStatusDef def, int actor, OrbitObject target,
                                                   out StatusSide side)
        {
            side = StatusSide.Unknown;
            bool master = IsMaster(actor);
            if (def.Stance == StatusStance.MasterOnly || def.Stance == StatusStance.Any || master)
                return RemovalVerdict(def.Stance, master, side);
            int force;
            side = SideOf(actor, target, out force);
            return RemovalVerdict(def.Stance, false, side);
        }

        private static string Reason(StatusRefusal r, EnemyStatusDef def, StatusSide side, int actor)
        {
            switch (r)
            {
                case StatusRefusal.NotMaster:
                    return "stance MasterOnly and actor " + actor + " is not the master client";
                case StatusRefusal.Stance:
                    return "stance " + def.Stance + " and the sender is " + SideText(side);
                case StatusRefusal.UnknownSide:
                    return "stance " + def.Stance + " and the sender's side is unknown " +
                           "(no character and no crew ship)";
                case StatusRefusal.Authority:
                    return "stance " + def.Stance + " and the sender is " + SideText(side) +
                           "; only a sender the stance lets apply it, or the master, may remove it";
                default:
                    return r.ToString();
            }
        }

        private static string SideText(StatusSide side)
        {
            switch (side)
            {
                case StatusSide.Same: return "on the target's side";
                case StatusSide.Other: return "on another side from the target";
                default: return "of unknown side";
            }
        }

        /// <summary>A refusal of this client's own call: once per operation and key as a
        /// warning when it came through the fire-and-forget method, which returns nothing;
        /// otherwise only with VerboseLogging, since the Try method returned it.</summary>
        private static void ReportOwnRefusal(string op, string key, string targetText, string reason,
                                             bool fireAndForget)
        {
            string line = "Ectype: " + op + "('" + key + "') on " + targetText + " refused: " + reason + ".";
            if (fireAndForget && WarnedOnce.Add("own-refusal:" + op + ":" + key))
                Plugin.Log.LogWarning(line + " Logged once per status; Try" + op + " returns the reason.");
            else if (Plugin.Verbose)
                Plugin.Log.LogInfo(line);
        }

        /// <summary>Warnings written by <see cref="ReportReceivedRefusal"/> for a MasterOnly status
        /// since load (read by the dev self-test).</summary>
        internal static int NotMasterLines;

        /// <summary>
        /// A refused received message. A MasterOnly refusal is logged once per sender and status,
        /// since a client that is not the master sending one is either modified or raced a master
        /// handover; any other refusal goes through the throttled <see cref="StatusRefusalLog"/>.
        /// </summary>
        private static void ReportReceivedRefusal(int sender, string op, string key, string targetText,
                                                  StatusRefusal r, EnemyStatusDef def, StatusSide side)
        {
            string line = "Ectype: refused enemy status '" + key + "' " + op + " from actor " + sender +
                          " on " + targetText + ": " + Reason(r, def, side, sender) + ".";
            if (r == StatusRefusal.NotMaster)
            {
                if (!WarnedOnce.Add("notmaster:" + sender + ":" + key)) return;
                Plugin.Log.LogWarning(line + " Further refusals of this status from this actor are not logged.");
                NotMasterLines++;
                return;
            }
            StatusRefusalLog.Report(sender, op, key, line);
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
            try { FlushOutbox(false); }
            catch (Exception e)
            {
                // Isolated so a failed send never skips this frame's expiry pump; the batches
                // were taken out before sending, so a fault cannot repeat on the same one.
                if (WarnedOnce.Add("flush"))
                    Plugin.Log.LogError("Ectype: sending coalesced status applications failed " +
                                        "(reported once): " + e);
            }
            // Before the markers and in its own try, so a fault in either cannot stop part expiry.
            try { PollParts(); }
            catch (Exception e)
            {
                if (WarnedOnce.Add("pollparts"))
                    Plugin.Log.LogError("Ectype: ending part statuses failed (reported once): " + e);
            }
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
            // Only this life: a successor under the same key (an OnRemoved that re-applied
            // it) stays registered, and its batch stays open.
            EnemyStatusEffect current;
            if (!Active.TryGetValue(fx.MapKey, out current) || !ReferenceEquals(current, fx)) return;
            Active.Remove(fx.MapKey);
            // The life this batch belonged to is over: close it so a later application opens
            // a new one. A removal drops it right after.
            Outbox.Close(fx.ViewId, fx.Def.Key);
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
                                      "a further status takes the slot of the farthest one only " +
                                      "when clearly nearer, else it runs without " + omitted +
                                      " until one frees up ([EnemyStatuses] " + configKey + ").");
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

        /// <summary>Coalesced applications waiting for their window (CoalesceMs).</summary>
        internal static readonly EnemyStatusOutbox Outbox = new EnemyStatusOutbox();
        private static readonly List<EnemyStatusOutbox.Batch> FlushScratch =
            new List<EnemyStatusOutbox.Batch>();

        /// <summary>Sends every batch whose window has passed (every batch when
        /// <paramref name="all"/>) as one ApplyN message each.</summary>
        internal static void FlushOutbox(bool all)
        {
            if (Outbox.Count == 0) return;
            FlushScratch.Clear();
            Outbox.TakeDue(Time.unscaledTime, all, FlushScratch);
            if (FlushScratch.Count == 0) return;
            Player[] to = RuntimeAbilities.Recipients();
            for (int i = 0; i < FlushScratch.Count; i++)
            {
                // Sent even when this client's copy has ended: a peer whose copy runs later
                // (another client extended it) still adds the stacks; one without a copy
                // ignores a batch past its end.
                EnemyStatusOutbox.Batch b = FlushScratch[i];
                if (to.Length > 0)
                    EctypeNet.Send(EctypeNet.EnemyStatusStacks, to, b.Args(), reliable: true);
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + b.Key + "' on " + b.TargetName + ": " +
                                       b.Applications + " application(s) in one " + b.WindowMs +
                                       " ms window, " + (to.Length > 0 ? "1 send" : "0 sends (no peers)") +
                                       ".");
            }
            FlushScratch.Clear();
        }

        /// <summary>The fields of one object-target application. A Tag of 0 travels in the
        /// untagged layout, so an untagged application keeps the format it always had.</summary>
        internal static object[] ApplyArgs(int viewId, string key, int startTs, int durationMs,
                                           float magnitude, int attackerView, int tag)
        {
            return tag == 0
                       ? new object[] { (byte)0, viewId, key, startTs, durationMs, magnitude, attackerView }
                       : new object[] { (byte)0, viewId, key, startTs, durationMs, magnitude, attackerView, tag };
        }

        /// <summary>
        /// One received apply or remove (subtype <see cref="EctypeNet.EnemyStatus"/>):
        /// <c>[byte op, int viewId, string key]</c>, op 0 adding
        /// <c>int startTs, int durationMs, float magnitude, int attackerView</c> and, when
        /// tagged, <c>int tag</c>; op 1 adding <c>int removedTs</c>; ops 2 and 3 are the part
        /// target's apply and remove (<see cref="HandlePart"/>). Fields that do not fit a layout
        /// of the op they name are refused before any is read. <paramref name="sender"/> is the
        /// actor the event came from. Returns what was refused, for the dev self-test; a refusal
        /// is logged here.
        /// </summary>
        internal static StatusRefusal HandleMessage(int sender, object[] args)
        {
            if (!EctypeNet.FitsEnemyStatusOp(args)) return StatusRefusal.Failed;
            byte op = (byte)args[0];
            int viewId = (int)args[1];
            string key = args[2] as string;
            if (key == null) return StatusRefusal.Failed;
            if (op == 2 || op == 3) return HandlePart(sender, op, viewId, key, args);

            OrbitObject target = ResolveTarget(viewId);
            if (target == null) return StatusRefusal.InvalidTarget;   // not here yet, or gone

            if (op == 1) return ReceiveRemove(sender, target, key, (int)args[3]);

            EnemyStatusDef def = ReceivedDef(key);
            if (def == null) return StatusRefusal.NotRegistered;
            int tag = args.Length >= 8 ? (int)args[7] : 0;
            return ReceiveApply(sender, target, def, (int)args[3], (int)args[4], (float)args[5],
                                (int)args[6], tag, 1);
        }

        /// <summary>
        /// One received batch of coalesced applications (subtype
        /// <see cref="EctypeNet.EnemyStatusStacks"/>): <c>[int viewId, string key, int startTs,
        /// int durationMs, float magnitude, int attackerView, int applications]</c>, and
        /// <c>int tag</c> when tagged. Judged like a single apply, then applied as that many
        /// applications with the timer moved once.
        /// </summary>
        internal static StatusRefusal HandleStacks(int sender, object[] args)
        {
            if (args == null || args.Length < 7) return StatusRefusal.Failed;
            string key = args[1] as string;
            int applications = (int)args[6];
            int tag = args.Length >= 8 ? (int)args[7] : 0;
            if (key == null || applications < 1) return StatusRefusal.Failed;
            if (applications > EnemyStatusOutbox.MaxApplications)
                applications = EnemyStatusOutbox.MaxApplications;

            OrbitObject target = ResolveTarget((int)args[0]);
            if (target == null) return StatusRefusal.InvalidTarget;
            EnemyStatusDef def = ReceivedDef(key);
            if (def == null) return StatusRefusal.NotRegistered;
            int startTs = (int)args[2], durationMs = (int)args[3];
            if (unchecked(startTs + durationMs - PhotonNetwork.ServerTimestamp) <= 0 &&
                Find(target, key) == null)
            {
                // The applier's status ended before this batch arrived and this client has no
                // copy left: creating one would start a status already over. A copy still
                // running here (extended by another client) takes the stacks instead.
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + key + "' on " + target.name + ": a batch of " +
                                       applications + " application(s) from actor " + sender +
                                       " arrived after the status ended; ignored.");
                return StatusRefusal.InvalidDuration;
            }
            return ReceiveApply(sender, target, def, startTs, durationMs, (float)args[4],
                                (int)args[5], tag, applications);
        }

        private static OrbitObject ResolveTarget(int viewId)
        {
            PhotonView view = PhotonView.Find(viewId);
            return view != null ? view.GetComponent<OrbitObject>() : null;
        }

        private static EnemyStatusDef ReceivedDef(string key)
        {
            EnemyStatusDef def;
            if (Defs.TryGetValue(key, out def)) return def;
            // This peer lacks the registering mod. Documented degradation.
            if (WarnedOnce.Add("nodef:" + key))
                Plugin.Log.LogWarning("Ectype: peer applied unknown enemy status '" +
                                      key + "'; ignoring (mod not installed here?).");
            return null;
        }

        private static StatusRefusal ReceiveApply(int sender, OrbitObject target, EnemyStatusDef def,
                                                  int startTs, int durationMs, float magnitude,
                                                  int attackerView, int tag, int stacks)
        {
            if (SessionData.PredatesSession(startTs))
            {
                // Raised in the previous session, dispatched after the new scene loaded: the view
                // id may now name a different object.
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + def.Key + "' on " + target.name +
                                       ": an application from actor " + sender + " started before " +
                                       "this client's current session; ignored.");
                return StatusRefusal.InvalidDuration;
            }
            if (Tombstones.Covers(target.photonView.ViewID, def.Key, startTs, PhotonNetwork.ServerTimestamp))
            {
                // Sent before the sender learned of a removal this client already processed.
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + def.Key + "' on " + target.name +
                                       ": an application from actor " + sender + " started before " +
                                       "its removal here; ignored.");
                return StatusRefusal.InvalidDuration;
            }
            PhotonView av = attackerView != 0 ? PhotonView.Find(attackerView) : null;
            OrbitObject attacker = av != null ? av.GetComponent<OrbitObject>() : null;
            int senderForce;
            bool byAttacker;
            StatusSide side = SenderSide(sender, target, attacker, out senderForce, out byAttacker);
            StatusRefusal stance = ApplicationVerdict(def.Stance, IsMaster(sender), side);
            if (stance != StatusRefusal.None && attackerView != 0 && attacker == null && IsMaster(sender))
            {
                // The master named an attacker this client can no longer find: an NPC that died
                // or despawned while a coalesced batch waited. Only the master applies on an
                // NPC's behalf, and it judged the NPC's side when it applied.
                stance = StatusRefusal.None;
                byAttacker = true;
                senderForce = -1;
            }
            if (stance != StatusRefusal.None)
            {
                ReportReceivedRefusal(sender, "apply", def.Key, target.name, stance, def, side);
                return stance;
            }
            bool created;
            ApplyLocal(target, def, startTs, durationMs, magnitude, attacker, sender, senderForce,
                       byAttacker, tag, stacks, out created);
            return StatusRefusal.None;
        }

        private static StatusRefusal ReceiveRemove(int sender, OrbitObject target, string key, int removedTs)
        {
            EnemyStatusDef def;
            if (!Defs.TryGetValue(key, out def)) return StatusRefusal.NotRegistered;
            StatusSide side;
            StatusRefusal verdict = JudgeRemoval(def, sender, target, out side);
            if (verdict != StatusRefusal.None)
            {
                if (verdict == StatusRefusal.NotMaster || Find(target, key) != null)
                    ReportReceivedRefusal(sender, "remove", key, target.name, verdict, def, side);
                return verdict;
            }
            int viewId = target.photonView.ViewID;
            // This client's own queued applications would re-create the status on every
            // other peer after the removal. Dropped and remembered before the local removal runs
            // its callbacks, so an application a callback makes is neither dropped nor covered.
            Outbox.Drop(viewId, key);
            RecordRemoval(viewId, def, RemovalTime(removedTs));
            RemoveLocal(target, key);
            return StatusRefusal.None;
        }

        /// <summary>
        /// The server time a received removal is remembered from: the remover's, so an
        /// application the remover makes after it is new on every client however long the
        /// removal took to arrive. One later than this client's clock is taken as now, so a
        /// sender cannot hold off applications past the removal memory.
        /// </summary>
        internal static int RemovalTime(int removedTs)
        {
            int now = PhotonNetwork.ServerTimestamp;
            return unchecked(removedTs - now) > 0 ? now : removedTs;
        }

        /// <summary>Recent removals: an application started before one is ignored while it lasts.</summary>
        internal static readonly EnemyStatusTombstones Tombstones = new EnemyStatusTombstones();

        /// <summary>How long a removal is remembered: one coalescing window plus two seconds of
        /// delivery, longer than anything in flight when it was made.</summary>
        internal static int RemovalMemoryMs(EnemyStatusDef def)
        {
            return Math.Max(0, def.CoalesceMs) + 2000;
        }

        /// <summary>Margin on top of CoalesceMs below which a duration may end before its batch
        /// reaches a peer: the frame the flush waits for plus delivery.</summary>
        internal const int ShortDurationMarginMs = 500;

        private static void RecordRemoval(int viewId, EnemyStatusDef def, int removedTs)
        {
            Tombstones.Record(viewId, def.Key, removedTs, RemovalMemoryMs(def));
        }
    }

    /// <summary>Which relation between the client applying a status and its target the status
    /// accepts (<see cref="EnemyStatusDef.Stance"/>).</summary>
    public enum StatusStance
    {
        /// <summary>No check: any client may apply the status to any target. The default.</summary>
        Any = 0,
        /// <summary>Only a client on another side from the target: a debuff or damage over time
        /// meant for enemies.</summary>
        Hostile = 1,
        /// <summary>Only a client on the target's side: a buff meant for allies.</summary>
        Friendly = 2,
        /// <summary>Only the master client, for applications and removals alike, whatever its
        /// side: a status the host decides, such as a window a host-run rule grants. Every
        /// receiver refuses one from any other client.</summary>
        MasterOnly = 3,
    }

    /// <summary>Which viewers draw a status's marker and visual
    /// (<see cref="EnemyStatusDef.Visibility"/>). Sides are game factions, as
    /// <see cref="Topology.ForceOf(Photon.Realtime.Player)"/> gives them for players.</summary>
    public enum StatusVisibility
    {
        /// <summary>Every viewer. The default.</summary>
        All = 0,
        /// <summary>Viewers on the side of the player who applied the status.</summary>
        OwnForce = 1,
        /// <summary>Viewers on the target's side: a warning to the afflicted crew.</summary>
        TargetForce = 2,
        /// <summary>Viewers on another side from the target, whoever applied it.</summary>
        HostileToTarget = 3,
    }

    /// <summary>Why an <c>EnemyStatuses.TryApply</c> or <c>EnemyStatuses.TryRemove</c>
    /// did nothing.</summary>
    public enum StatusRefusal
    {
        /// <summary>Not refused: applied or removed here and sent to the peers.</summary>
        None = 0,
        /// <summary>The target is null or has no PhotonView.</summary>
        InvalidTarget = 1,
        /// <summary>No template is registered under the key on this client.</summary>
        NotRegistered = 2,
        /// <summary>The framework's patches did not apply this session.</summary>
        Inert = 3,
        /// <summary>The duration is zero, negative or not finite.</summary>
        InvalidDuration = 4,
        /// <summary>SkipsInvulnerableTargets is set and the target is invulnerable.</summary>
        Invulnerable = 5,
        /// <summary>The sender's relation to the target fails the template's Stance.</summary>
        Stance = 6,
        /// <summary>The template has a Stance other than Any and the sender's side is unknown:
        /// no character and no crew ship.</summary>
        UnknownSide = 7,
        /// <summary>A removal from a client that is not the master and that the template's
        /// Stance would not let apply the status.</summary>
        Authority = 8,
        /// <summary>The call threw; the exception is logged.</summary>
        Failed = 9,
        /// <summary>The template's Stance is MasterOnly and this client is not the master.</summary>
        NotMaster = 10,
    }

    /// <summary>The relation of a sender to a status target.</summary>
    internal enum StatusSide { Unknown, Same, Other }

    /// <summary>
    /// Refusals of received status messages, logged as warnings at most once per
    /// <see cref="WindowSeconds"/> per (sender, operation, status key); the next line carries
    /// the count of refusals left unreported in between.
    /// </summary>
    internal static class StatusRefusalLog
    {
        internal const float WindowSeconds = 30f;

        private sealed class Slot
        {
            public float NextAt;
            public int Unreported;
        }

        private static readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>();

        /// <summary>Lines written since load (read by the dev self-test).</summary>
        internal static int Lines;

        /// <summary>Logs the line unless one for this sender, operation and key was logged
        /// within the window. True when it logged.</summary>
        internal static bool Report(int sender, string op, string key, string line)
        {
            string id = sender + "/" + op + "/" + key;
            float now = Time.unscaledTime;
            Slot slot;
            if (!Slots.TryGetValue(id, out slot))
            {
                slot = new Slot { NextAt = float.MinValue };
                Slots[id] = slot;
            }
            if (now < slot.NextAt)
            {
                slot.Unreported++;
                return false;
            }
            string tail = slot.Unreported > 0
                              ? " " + slot.Unreported + " more like it from this sender since the last report."
                              : "";
            slot.Unreported = 0;
            slot.NextAt = now + WindowSeconds;
            Plugin.Log.LogWarning(line + tail);
            Lines++;
            return true;
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
        private int _tag;

        /// <summary>The Tag of the latest accepted application.</summary>
        internal int Tag { get { return _tag; } }

        private StatusInstance Instance(OrbitObject target, bool created)
        {
            return new StatusInstance(_def.Key, target, default(StatusPart), false, _tag, _stacks, _endTs, created);
        }

        internal float RemainingSeconds
        {
            get
            {
                int ms = unchecked(_endTs - PhotonNetwork.ServerTimestamp);
                return ms > 0 ? ms / 1000f : 0f;
            }
        }

        /// <summary>The target's view id.</summary>
        internal readonly int ViewId;

        /// <summary>The actor whose application created this status on this client: the sender
        /// of the message on a receiver, this client on the applier. Two clients that applied
        /// concurrently can disagree on it, so it serves logging and the damage-over-time
        /// recheck on the target's owner, never authority.</summary>
        internal readonly int ApplierActor;

        /// <summary>The creating applier's faction, -1 when unknown. Stands in for the applier's
        /// side in the tick recheck once that can no longer be resolved (the applier left).</summary>
        internal readonly int ApplierForce;

        /// <summary>The creating application was judged by its NPC attacker's side (the master
        /// applying on an NPC's behalf); the tick recheck then follows the attacker.</summary>
        internal readonly bool JudgedByAttacker;

        /// <summary>The sides of every applier accepted here, for the OwnForce visibility rule.
        /// Every client accepts the same reliable applications, so the set converges whatever
        /// order they arrive in.</summary>
        internal readonly List<int> ApplierSides = new List<int>(2);

        /// <summary>The hidden-by-visibility line was logged for the current hidden spell.</summary>
        internal bool HiddenLogged;

        private bool _tickSkipLogged;

        internal EnemyStatusEffect(EnemyStatusDef def, float magnitude, OrbitObject attacker,
                                   int viewId, string mapKey, int startTs, int durationMs,
                                   int applierActor, int applierForce, bool judgedByAttacker, int tag)
        {
            _def = def;
            _tag = tag;
            _magnitude = magnitude;
            _attacker = attacker;
            ViewId = viewId;
            ApplierActor = applierActor;
            ApplierForce = applierForce;
            JudgedByAttacker = judgedByAttacker;
            NoteApplierSide(applierForce);
            MapKey = mapKey;
            _activationTs = startTs;
            _endTs = unchecked(startTs + durationMs);
            int now = PhotonNetwork.ServerTimestamp;
            int grace = CatchUpGraceMs(def);
            _catchUpFrom = CatchUpFrom(now, startTs, grace);
            _lastTick = FirstUnpaidTick(now, startTs, TickMs(def), grace);
            ArmBaseTimer();
        }

        /// <summary>Server timestamp the status started at; the ticks are indexed from it.</summary>
        internal int ActivationTs { get { return _activationTs; } }

        /// <summary>Where this client's catch-up for ticks and suspension begins: the start,
        /// or, when the status reached this client later than the grace, the grace before its
        /// arrival. A batch carries the start of the sender's whole life of the status, and a
        /// copy created here from it must not owe what happened before it existed here.</summary>
        private readonly int _catchUpFrom;

        /// <summary>How late a status may reach this client and still pay everything since its
        /// start: one coalescing window plus half a second of delivery.</summary>
        internal static int CatchUpGraceMs(EnemyStatusDef def)
        {
            return Math.Max(0, def.CoalesceMs) + 500;
        }

        internal static int TickMs(EnemyStatusDef def)
        {
            return (int)(Math.Max(def.TickSeconds, 0.05f) * 1000f);
        }

        /// <summary>The later of the start and <paramref name="now"/> minus the grace (wrap-safe).</summary>
        internal static int CatchUpFrom(int now, int startTs, int graceMs)
        {
            int floor = unchecked(now - graceMs);
            return unchecked(floor - startTs) > 0 ? floor : startTs;
        }

        /// <summary>The tick index already counted as paid when a copy is created at
        /// <paramref name="now"/>: every tick due before <see cref="CatchUpFrom"/>. Ticks due
        /// within the grace are still paid, so a status that arrives a little late pays them
        /// once, and one that arrives late by more pays nothing it missed.</summary>
        internal static long FirstUnpaidTick(int now, int startTs, int tickMs, int graceMs)
        {
            long skipped = unchecked(CatchUpFrom(now, startTs, graceMs) - startTs);
            return skipped > 0 ? (skipped - 1) / tickMs : 0;
        }

        internal void NoteApplierSide(int side)
        {
            if (side >= 0 && !ApplierSides.Contains(side)) ApplierSides.Add(side);
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

        /// <summary><paramref name="applications"/> re-applications at once: the timer moves
        /// once, then each application stacks or, at MaxStacks, reports through OnApplied,
        /// exactly as that many single re-applications would. A refresh only ever moves the end
        /// later, so applications from several clients leave the same end in any arrival order.</summary>
        internal void Reapply(int startTs, int durationMs, int applications, int tag)
        {
            _tag = tag;
            int end = unchecked(startTs + durationMs);
            if (_def.RefreshOnReapply && unchecked(end - _endTs) > 0)
            {
                _endTs = end;
                ArmBaseTimer();
            }
            AddStacks(applications);
            Presentation(true);   // remove + re-add restarts the countdown circle
        }

        /// <summary>Adds applications without touching the timer or the presentation.</summary>
        internal void AddStacks(int applications)
        {
            for (int i = 0; i < applications; i++)
            {
                if (_stacks < _def.MaxStacks) ApplyStack();
                else ReportApplied(false);
            }
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
            // the status reached a little late freezes the same span as the one that applied
            // it; one it reached later than the grace starts the freeze just before arrival.
            if (_def.SuspendsAbilities) EnemyAbilities.Begin(this, target, _catchUpFrom);
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
            // Out of the registry before the callback: an OnRemoved that applies the same key
            // again must start a new life, not stack onto this one, whose modifiers are gone.
            EnemyStatuses.Unregister(this);
            Presentation(false);
            if (_def.OnRemoved != null) SafeCb(() => _def.OnRemoved(t));
            if (_def.OnInstanceRemoved != null)
            {
                StatusInstance ended = Instance(t, false);
                SafeCb(() => _def.OnInstanceRemoved(ended));
            }
        }

        /// <summary>The applied callbacks for one application, with the stacks it left.</summary>
        private void ReportApplied(bool created)
        {
            if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_tgt, _stacks));
            if (_def.OnInstanceApplied != null)
            {
                StatusInstance now = Instance(_tgt, created);
                SafeCb(() => _def.OnInstanceApplied(now));
            }
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
            // Every life starts at no stacks, so the first stack is the creating application.
            bool created = _stacks == 0;
            _stacks++;
            ReportApplied(created);
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
            if (_lastTick >= due) return;
            // A Hostile status pays a tick only while its applier is still on another side
            // from the target (a team change, a captured ship). Skipped ticks are not owed.
            bool skip = _def.Stance == StatusStance.Hostile && !ApplierStillHostile(target);
            while (_lastTick < due)
            {
                _lastTick++;
                if (skip) continue;
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


        /// <summary>The applier's side now (its NPC attacker's, when the status was applied on an
        /// NPC's behalf), else the side recorded when the status was created.</summary>
        private bool ApplierStillHostile(OrbitObject target)
        {
            int force;
            StatusSide side;
            if (JudgedByAttacker)
                side = _attacker != null
                           ? (Topology.Hostile(_attacker, target) ? StatusSide.Other : StatusSide.Same)
                           : StatusSide.Unknown;
            else
                side = EnemyStatuses.SideOf(ApplierActor, target, out force);
            // Unknown with no recorded side: the master applied for an NPC this client never
            // resolved, and is trusted for it.
            bool hostile = side == StatusSide.Other ||
                           (side == StatusSide.Unknown && ApplierForce >= 0 && ApplierForce != target.Faction) ||
                           (side == StatusSide.Unknown && ApplierForce < 0 && JudgedByAttacker);
            if (hostile) _tickSkipLogged = false;
            else if (!_tickSkipLogged)
            {
                _tickSkipLogged = true;
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + _def.Key + "' on " + target.name +
                                       ": tick skipped, applier actor " + ApplierActor +
                                       " is no longer hostile to it.");
            }
            return hostile;
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
    /// Not owner-gated: any crew member's client may apply a status to an NPC (the applier
    /// is not the view owner - the master is), and the payload can only reference templates
    /// this client itself registered. The sender's actor is judged against the template's
    /// Stance, for an application and for a removal alike; the master may always remove.
    /// </summary>
    internal static class EnemyStatusMessage
    {
        internal static void Receive(int senderActor, object[] args)
        {
            try
            {
                EnemyStatuses.HandleMessage(senderActor, args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling enemy-status message failed: " + e);
            }
        }

        /// <summary>Receiver for subtype <see cref="EctypeNet.EnemyStatusStacks"/>.</summary>
        internal static void ReceiveStacks(int senderActor, object[] args)
        {
            try
            {
                EnemyStatuses.HandleStacks(senderActor, args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling enemy-status message failed: " + e);
            }
        }
    }
}
