using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using CG.Game;
using CG.Game.SpaceObjects.Controllers;
using CG.Game.SpaceObjects.Controllers.ReinforcementCalling;
using CG.Ship.Modules.Weapons.SpacecraftTurrets;
using CG.Space;
using Gameplay.NPC;
using Gameplay.NPC.AI;
using Gameplay.Utilities;
using HarmonyLib;
using Photon.Pun;
using UI.ObjectMarker;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>Where an enemy ability is in its cycle.</summary>
    public enum EnemyAbilityPhase
    {
        /// <summary>Available; starts when whatever drives it next asks.</summary>
        Ready,
        /// <summary>Winding up before its effect.</summary>
        Casting,
        /// <summary>Its effect is running and will end on its own.</summary>
        Channeling,
        /// <summary>Its effect is running with no set end: a passive, a toggle, a latch.</summary>
        Sustained,
        /// <summary>Waiting to become available again.</summary>
        Cooldown,
        /// <summary>A once-per-life ability that has been used.</summary>
        Spent,
    }

    /// <summary>One ability on an enemy, as <see cref="EnemyAbilities.Describe"/> reports it.</summary>
    public sealed class EnemyAbilityInfo
    {
        /// <summary>The object that owns the ability: the enemy, or one of its turrets.</summary>
        public readonly OrbitObject Host;
        /// <summary>Which game system runs it: npc-ability, sniper-cycle, debuff-latch,
        /// stat-aura, shield-pulse, reinforcement-behaviour, reinforcement-caller.</summary>
        public readonly string Kind;
        /// <summary>The ability's display name.</summary>
        public readonly string Name;
        /// <summary>Where the ability is in its cycle.</summary>
        public readonly EnemyAbilityPhase Phase;

        internal EnemyAbilityInfo(OrbitObject host, string kind, string name, EnemyAbilityPhase phase)
        {
            Host = host;
            Kind = kind;
            Name = name;
            Phase = phase;
        }

        /// <summary>"Name [Kind, Phase]".</summary>
        public override string ToString() { return Name + " [" + Kind + ", " + Phase + "]"; }
    }

    /// <summary>
    /// Enemy abilities as one model over the game's several systems, and the suspension an
    /// enemy status can impose on them (<see cref="EnemyStatusDef.SuspendsAbilities"/>).
    ///
    /// WHAT COUNTS AS AN ABILITY. Enemies start special actions through seven systems, and
    /// each is classified by its game type, so any enemy assembled from them - a future
    /// stock enemy or a modded one - is covered without a list of enemies:
    ///
    ///   npc-ability              every BaseNpcAbility on an NpcAI (boss volleys, cones,
    ///                            channelled debuffs, repairs, summons, auras, passives)
    ///   sniper-cycle             BehSniperAttack: warm-up and lock-on (cast), volley
    ///                            (channel), post-fire wait (cooldown)
    ///   debuff-latch             BehDebuffApplier: approach, latch (sustained), orbit wait
    ///   stat-aura                BehStatModifyWhileOrbiting: a buff held on an ally
    ///   shield-pulse             BehShieldGenerateAOE: a pulse on a fixed period
    ///   reinforcement-behaviour  BehSpawnReinforcements' call
    ///   reinforcement-caller     the NpcReinforcementCaller component's call
    ///
    /// Basic weapon fire and movement are not abilities and are never touched. A behaviour
    /// type outside the lists here is logged once when an enemy carrying it is described.
    ///
    /// SUSPENSION freezes all of it in place: casts hold where they are, channelled and
    /// sustained effects are lifted, cooldowns stop, and nothing new starts. When it ends,
    /// everything resumes from where it stopped. Four mechanisms, each the game's own state:
    ///
    ///   Ticks are skipped.  Every ability's Tick and PassiveTick, the four ability
    ///                       behaviours' steps, the two reinforcement calls, and the NPC
    ///                       weapon carrying a volley in progress. Movement inside those
    ///                       behaviours still runs; only their ability steps wait.
    ///   Clocks are moved.   Every timer these systems read is a server timestamp (the
    ///                       per-tick accumulators stop by themselves when their tick is
    ///                       skipped). Each frame of suspension moves those timestamps
    ///                       forward by the frame's server time, so a remaining cast, channel
    ///                       or cooldown is the same when the suspension ends. A timer the
    ///                       game resets mid-suspension (a boss phase change readying its
    ///                       abilities) only gets the frames after its reset. The total is
    ///                       capped at the status's own end, which every client shares, so
    ///                       clients agree however late the status reached them.
    ///   Effects are lifted. An ability's stat modifiers carry the ability as their source.
    ///                       Every modifier whose source belongs to the enemy is removed
    ///                       from whatever holds it and re-applied at the end, unless the
    ///                       ability changed state meanwhile (then its own code owns them).
    ///   Starts are held.    TryActivateAbility refuses non-forced calls. Forced calls are
    ///                       a replica copying its controller's state and pass, so replicas
    ///                       cannot disagree with a suspended controller. The two cast
    ///                       behaviours retry every tick; any other caller fires once (a
    ///                       boss's on-aggro list, a passive's OnEnable), so its refusal is
    ///                       replayed through the normal path when the suspension ends.
    ///
    /// A ship's turrets are suspended with it: the hit resolves to the ship, and a turret
    /// is its own object with its own AI. Reapplying a suspending status extends the same
    /// suspension; two suspending statuses on one enemy share it.
    ///
    /// The game's own cast and call markers over the enemy freeze with it and resume from
    /// the same point, so the countdown the crew sees stays honest.
    /// </summary>
    public static class EnemyAbilities
    {
        // ---------------------------------------------------------------- public surface

        /// <summary>Every ability on the target and its turrets, with its phase.</summary>
        public static List<EnemyAbilityInfo> Describe(OrbitObject target)
        {
            List<EnemyAbilityInfo> list = new List<EnemyAbilityInfo>();
            if (ReferenceEquals(target, null) || target == null) return list;
            try
            {
                foreach (OrbitObject host in HostObjects(target)) DescribeHost(host, list);
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("abilities:describe"))
                    Plugin.Log.LogError("Ectype: describing enemy abilities failed " +
                                        "(suppressing further reports): " + e);
            }
            return list;
        }

        /// <summary>True when the target or one of its turrets has any ability.</summary>
        public static bool HasAny(OrbitObject target) { return Describe(target).Count > 0; }

        /// <summary>True while the host's abilities are suspended on this client.</summary>
        public static bool IsSuspended(OrbitObject host) { return Frozen(host); }

        // ---------------------------------------------------------------- suspension state

        private sealed class Lifted
        {
            internal StatTagCollection Into;
            internal OrbitObject Holder;
            internal object Source;
            internal List<StatMod> Mods;
        }

        private sealed class FrozenMarker
        {
            internal ObjectMarkerData Data;
            internal MarkerVE Ve;
            internal float Progress;
            internal float AbilityTime;
        }

        private sealed class Host
        {
            internal OrbitObject Obj;
            internal NpcAI Ai;
            internal NpcReinforcementCaller Caller;
            internal int ShiftedTo;
            internal readonly List<EnemyStatusEffect> Covering = new List<EnemyStatusEffect>();
            internal readonly List<Lifted> LiftedSets = new List<Lifted>();
            internal readonly Dictionary<BaseNpcAbility, bool> ActiveAtStart =
                new Dictionary<BaseNpcAbility, bool>(ByRef<BaseNpcAbility>.Instance);
            internal readonly List<NpcWeapon> Weapons = new List<NpcWeapon>();
            internal FrozenMarker Marker;
            internal readonly List<BaseNpcAbility> Deferred = new List<BaseNpcAbility>();
            internal readonly List<ActShieldTargets> DeferredShields = new List<ActShieldTargets>();
            internal int Reported;
        }

        private static readonly Dictionary<OrbitObject, Host> Hosts =
            new Dictionary<OrbitObject, Host>(ByRef<OrbitObject>.Instance);

        private static readonly Dictionary<EnemyStatusEffect, List<Host>> ByEffect =
            new Dictionary<EnemyStatusEffect, List<Host>>(ByRef<EnemyStatusEffect>.Instance);

        /// <summary>Where a host's last suspension left its clocks, so a status that lands
        /// on one client after the previous one already ended there does not shift the
        /// overlap twice.</summary>
        private static readonly Dictionary<OrbitObject, int> LastShifted =
            new Dictionary<OrbitObject, int>(ByRef<OrbitObject>.Instance);

        private static readonly HashSet<NpcWeapon> FrozenWeapons =
            new HashSet<NpcWeapon>(ByRef<NpcWeapon>.Instance);

        /// <summary>Held one-shot starts, run on the frame after their suspension ends.</summary>
        private static readonly List<Action> DueReplays = new List<Action>();

        /// <summary>Above zero while a cast behaviour's tick runs.</summary>
        private static int _retryingCaller;

        private const int REPORT_ABILITY = 1, REPORT_CALLER = 2, REPORT_BEHAVIOUR = 4;

        private static bool Frozen(OrbitObject o)
        {
            return Hosts.Count != 0 && !ReferenceEquals(o, null) && Hosts.ContainsKey(o);
        }

        private static bool After(int a, int b) { return unchecked(a - b) > 0; }

        // ---------------------------------------------------------------- lifecycle

        /// <summary>A suspending status landed on this client. Idempotent per effect.</summary>
        internal static void Begin(EnemyStatusEffect effect, OrbitObject target, int startTs)
        {
            if (effect == null || ReferenceEquals(target, null) || ByEffect.ContainsKey(effect))
                return;
            try
            {
                // Recorded before any host is acquired, so End releases whatever was
                // acquired even if a later host throws.
                List<Host> list = new List<Host>();
                ByEffect[effect] = list;
                foreach (OrbitObject obj in HostObjects(target))
                {
                    try
                    {
                        Host h = Acquire(obj, startTs, effect);
                        list.Add(h);
                        // A client the status reached late catches up the frames since it began.
                        ShiftTo(h, Cap(h, PhotonNetwork.ServerTimestamp));
                    }
                    catch (Exception e)
                    {
                        if (EnemyStatuses.WarnOnce("abilities:acquire"))
                            Plugin.Log.LogError("Ectype: suspending one ability host failed " +
                                                "(suppressing further reports): " + e);
                    }
                }
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: abilities suspended on " + target.name + " (" +
                                       list.Count + " host(s)): " +
                                       string.Join(", ", Describe(target)));
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("abilities:begin"))
                    Plugin.Log.LogError("Ectype: suspending enemy abilities failed " +
                                        "(suppressing further reports): " + e);
            }
        }

        /// <summary>A suspending status ended on this client, at endTs on the server
        /// clock (its natural expiry, or now when removed early).</summary>
        internal static void End(EnemyStatusEffect effect, int endTs)
        {
            List<Host> list;
            if (effect == null || !ByEffect.TryGetValue(effect, out list)) return;
            ByEffect.Remove(effect);
            for (int i = 0; i < list.Count; i++)
            {
                try { Release(list[i], effect, endTs); }
                catch (Exception e)
                {
                    if (EnemyStatuses.WarnOnce("abilities:end"))
                        Plugin.Log.LogError("Ectype: resuming enemy abilities failed " +
                                            "(suppressing further reports): " + e);
                }
            }
        }

        private static Host Acquire(OrbitObject obj, int startTs, EnemyStatusEffect effect)
        {
            Host h;
            if (!Hosts.TryGetValue(obj, out h))
            {
                h = new Host
                {
                    Obj = obj,
                    Ai = obj.GetComponent<NpcAI>(),
                    Caller = obj.GetComponent<NpcReinforcementCaller>(),
                };
                int from = startTs, last;
                if (LastShifted.TryGetValue(obj, out last) && After(last, from)) from = last;
                h.ShiftedTo = from;
                Hosts[obj] = h;
                Suspend(h);
            }
            if (!h.Covering.Contains(effect)) h.Covering.Add(effect);
            return h;
        }

        private static void Release(Host h, EnemyStatusEffect effect, int endTs)
        {
            if (After(endTs, h.ShiftedTo)) ShiftTo(h, endTs);
            h.Covering.Remove(effect);
            if (h.Covering.Count > 0) return;
            Hosts.Remove(h.Obj);
            LastShifted[h.Obj] = h.ShiftedTo;
            if (LastShifted.Count > 64) PruneLastShifted();
            Resume(h);
        }

        /// <summary>No later than the latest end among the statuses covering it.</summary>
        private static int Cap(Host h, int now)
        {
            if (h.Covering.Count == 0) return now;
            int end = h.Covering[0].EndTs;
            for (int i = 1; i < h.Covering.Count; i++)
                if (After(h.Covering[i].EndTs, end)) end = h.Covering[i].EndTs;
            return After(now, end) ? end : now;
        }

        /// <summary>Driven every frame after the status pump, so an expiry this frame
        /// has already released its hosts before anything is moved.</summary>
        internal static void Poll()
        {
            if (Hosts.Count > 0)
            {
                int now = PhotonNetwork.ServerTimestamp;
                foreach (Host h in new List<Host>(Hosts.Values))
                {
                    try { ShiftTo(h, Cap(h, now)); }
                    catch (Exception e)
                    {
                        if (EnemyStatuses.WarnOnce("abilities:shift"))
                            Plugin.Log.LogError("Ectype: moving suspended ability clocks " +
                                                "failed (suppressing further reports): " + e);
                    }
                }
            }
            if (DueReplays.Count > 0) Replay();
        }

        private static void PruneLastShifted()
        {
            List<OrbitObject> dead = new List<OrbitObject>();
            foreach (OrbitObject o in LastShifted.Keys) if (o == null) dead.Add(o);
            for (int i = 0; i < dead.Count; i++) LastShifted.Remove(dead[i]);
        }

        // ---------------------------------------------------------------- hosts

        /// <summary>The target, plus every object under it that runs abilities, plus
        /// turrets attached by instantiation data rather than by the transform tree.</summary>
        private static List<OrbitObject> HostObjects(OrbitObject target)
        {
            List<OrbitObject> list = new List<OrbitObject>();
            HashSet<OrbitObject> seen = new HashSet<OrbitObject>(ByRef<OrbitObject>.Instance);
            AddHosts(target.gameObject, list, seen);
            var sector = GameSessionManager.ActiveSector;
            TargetableShipsCategory ships =
                sector == null ? null : sector.GetSectorObjectCategory<TargetableShipsCategory>();
            if (ships != null)
            {
                foreach (SpaceCraft s in ships.SectorObjects)
                {
                    Turret t = s as Turret;
                    if (t == null || ReferenceEquals(t, target)) continue;
                    if (ReferenceEquals(t.ParentSpaceCraft, target)) AddHosts(t.gameObject, list, seen);
                }
            }
            return list;
        }

        private static void AddHosts(GameObject root, List<OrbitObject> list,
                                     HashSet<OrbitObject> seen)
        {
            foreach (NpcAI ai in root.GetComponentsInChildren<NpcAI>(true))
                if (ai.OrbitObject != null && seen.Add(ai.OrbitObject)) list.Add(ai.OrbitObject);
            foreach (NpcReinforcementCaller c in root.GetComponentsInChildren<NpcReinforcementCaller>(true))
                if (c.OrbitObject != null && seen.Add(c.OrbitObject)) list.Add(c.OrbitObject);
        }

        // ---------------------------------------------------------------- clocks

        private static void ShiftTo(Host h, int ts)
        {
            int delta = unchecked(ts - h.ShiftedTo);
            if (delta <= 0) return;
            Shift(h, delta);
            h.ShiftedTo = ts;
        }

        private static void Shift(Host h, int ms)
        {
            if (h.Ai != null)
            {
                if (_lastUsage != null)
                    foreach (BaseNpcAbility a in h.Ai.Abilities)
                    {
                        BaseToggleableAbility t = a as BaseToggleableAbility;
                        if (t != null) _lastUsage(t) = unchecked(_lastUsage(t) + ms);
                    }
                if (h.Ai.Behaviours != null)
                    foreach (BaseBehaviour b in h.Ai.Behaviours) ShiftBehaviour(b, ms);
            }
            if (h.Caller != null && _isCalling != null && _callStart != null && _isCalling(h.Caller))
                _callStart(h.Caller) = unchecked(_callStart(h.Caller) + ms);
        }

        private static void ShiftBehaviour(BaseBehaviour b, int ms)
        {
            BehSniperAttack sniper = b as BehSniperAttack;
            if (sniper != null)
            {
                if (_sniperPreIdle != null) ShiftTimer(_sniperPreIdle(sniper), ms);
                if (_sniperPostIdle != null) ShiftTimer(_sniperPostIdle(sniper), ms);
                return;
            }
            BehDebuffApplier latch = b as BehDebuffApplier;
            if (latch != null)
            {
                if (_latchTimer != null) ShiftTimer(_latchTimer(latch), ms);
                return;
            }
            BehShieldGenerateAOE shield = b as BehShieldGenerateAOE;
            if (shield != null)
            {
                shield.AbilityStartTime = unchecked(shield.AbilityStartTime + ms);
                return;
            }
            BehSpawnReinforcements beh = b as BehSpawnReinforcements;
            if (beh != null && _reinforceAction != null && _reinforceStart != null)
            {
                ActReinforce act = _reinforceAction(beh);
                if (act != null && CallInProgress(act))
                    _reinforceStart(act) = unchecked(_reinforceStart(act) + ms);
            }
        }

        private static void ShiftTimer(ActTimer timer, int ms)
        {
            if (timer != null && _timerStart != null)
                _timerStart(timer) = unchecked(_timerStart(timer) + ms);
        }

        private static bool CallInProgress(ActReinforce act)
        {
            return !act.IsDone() && _unitsReinforcing != null && _unitsReinforcing(act) != null;
        }

        // ---------------------------------------------------------------- suspend / resume

        private static void Suspend(Host h)
        {
            if (h.Ai != null)
                foreach (BaseNpcAbility a in h.Ai.Abilities)
                    if (a != null) h.ActiveAtStart[a] = a.IsActive;
            Guarded("lift", () => LiftEffects(h));
            Guarded("weapons", () => FreezeWeapons(h));
            Guarded("marker", () => FreezeMarker(h));
        }

        private static void Resume(Host h)
        {
            int restored = 0;
            Guarded("restore", () => restored = RestoreEffects(h));
            for (int i = 0; i < h.Weapons.Count; i++) FrozenWeapons.Remove(h.Weapons[i]);
            Guarded("marker", () => ThawMarker(h));
            foreach (BaseNpcAbility a in h.Deferred)
            {
                BaseNpcAbility x = a;
                DueReplays.Add(() => ReplayStart(x));
            }
            foreach (ActShieldTargets c in h.DeferredShields)
            {
                ActShieldTargets x = c;
                DueReplays.Add(() => ReplayShield(x));
            }
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: abilities resumed on " +
                                   (h.Obj != null ? h.Obj.name : "a destroyed object") + ": " +
                                   restored + " effect set(s) restored, " + h.Weapons.Count +
                                   " weapon(s) released, " +
                                   (h.Deferred.Count + h.DeferredShields.Count) +
                                   " start(s) to replay.");
        }

        private static void Guarded(string step, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("abilities:" + step))
                    Plugin.Log.LogError("Ectype: ability suspension step '" + step + "' failed " +
                                        "(suppressing further reports): " + e);
            }
        }

        /// <summary>Everything whose modifiers count as this host's ability effects.</summary>
        private static HashSet<object> SourcesOf(Host h)
        {
            HashSet<object> set = new HashSet<object>(ByRef<object>.Instance);
            if (h.Ai == null) return set;
            foreach (BaseNpcAbility a in h.Ai.Abilities) if (a != null) set.Add(a);
            if (h.Ai.Behaviours != null)
                foreach (BaseBehaviour b in h.Ai.Behaviours)
                    if (b != null && b.Actions != null)
                        foreach (BaseAction act in b.Actions)
                            if (act is ActApplyStatMod) set.Add(act);
            return set;
        }

        /// <summary>Every object an enemy ability can have modified: the sector's ships,
        /// the players, the player ship and the host itself.</summary>
        private static List<OrbitObject> Candidates(Host h)
        {
            List<OrbitObject> list = new List<OrbitObject>();
            HashSet<OrbitObject> seen = new HashSet<OrbitObject>(ByRef<OrbitObject>.Instance);
            Action<OrbitObject> add = o => { if (o != null && seen.Add(o)) list.Add(o); };
            add(h.Obj);
            var sector = GameSessionManager.ActiveSector;
            TargetableShipsCategory ships =
                sector == null ? null : sector.GetSectorObjectCategory<TargetableShipsCategory>();
            if (ships != null) foreach (SpaceCraft s in ships.SectorObjects) add(s);
            ClientGame game = ClientGame.Current;
            if (game != null)
            {
                add((object)game.PlayerShip as OrbitObject);
                if (game.Players != null) foreach (CG.Game.Player.Player p in game.Players) add(p);
            }
            return list;
        }

        private static void LiftEffects(Host h) { LiftSources(h, SourcesOf(h)); }

        private static void LiftSources(Host h, HashSet<object> sources)
        {
            if (sources.Count == 0) return;

            List<Lifted> found = new List<Lifted>();
            foreach (OrbitObject o in Candidates(h))
            {
                StatTagCollection c = o.Stats;
                if (c == null) continue;
                Dictionary<object, List<StatMod>> bySource =
                    new Dictionary<object, List<StatMod>>(ByRef<object>.Instance);
                Collect(c.ActiveModifiers, sources, bySource);
                Collect(c.InactiveModifiers, sources, bySource);
                foreach (KeyValuePair<object, List<StatMod>> kv in bySource)
                    found.Add(new Lifted { Into = c, Holder = o, Source = kv.Key, Mods = kv.Value });
            }

            // A modifier applied to a ship also sits in its child collections (turrets,
            // modules). Lifting and restoring at the outermost holder covers the children.
            for (int i = 0; i < found.Count; i++)
            {
                if (HeldHigher(found[i], found)) continue;
                found[i].Into.RemoveModifier((IModifierSource)found[i].Source);
                h.LiftedSets.Add(found[i]);
            }
        }

        private static void Collect(List<StatMod> mods, HashSet<object> sources,
                                    Dictionary<object, List<StatMod>> into)
        {
            if (mods == null) return;
            for (int i = 0; i < mods.Count; i++)
            {
                StatMod m = mods[i];
                object src = m == null || m.Mod == null ? null : m.Mod.Source;
                if (src == null || !sources.Contains(src)) continue;
                List<StatMod> list;
                if (!into.TryGetValue(src, out list)) { list = new List<StatMod>(); into[src] = list; }
                list.Add(m);
            }
        }

        private static bool HeldHigher(Lifted l, List<Lifted> all)
        {
            for (StatTagCollection p = l.Into.ParentCollection; p != null; p = p.ParentCollection)
                for (int i = 0; i < all.Count; i++)
                    if (ReferenceEquals(all[i].Into, p) && ReferenceEquals(all[i].Source, l.Source))
                        return true;
            return false;
        }

        private static int RestoreEffects(Host h)
        {
            int restored = 0;
            // A host destroyed or disabled while suspended has already run its abilities'
            // own cleanup, against modifiers that were lifted at the time. Restoring now
            // would leave them on their targets with nothing left to remove them.
            if (h.Obj == null || (h.Ai != null && !h.Ai.isActiveAndEnabled))
            {
                h.LiftedSets.Clear();
                return 0;
            }
            for (int i = 0; i < h.LiftedSets.Count; i++)
            {
                Lifted l = h.LiftedSets[i];
                if (l.Holder == null || !StillOwns(h, l)) continue;
                l.Into.ApplyModifiers(l.Mods, (IModifierSource)l.Source, true);
                restored++;
            }
            h.LiftedSets.Clear();
            return restored;
        }

        /// <summary>Whether the source still believes these modifiers are applied. An
        /// ability that started or stopped meanwhile, or a stat action whose target moved,
        /// manages its own modifiers from here on.</summary>
        private static bool StillOwns(Host h, Lifted l)
        {
            BaseNpcAbility a = l.Source as BaseNpcAbility;
            if (a != null)
            {
                bool was;
                return h.ActiveAtStart.TryGetValue(a, out was) && was == a.IsActive;
            }
            ActApplyStatMod act = l.Source as ActApplyStatMod;
            if (act != null)
            {
                if (!act.EffectApplied) return false;
                OrbitObject t = act.Target;
                if (t != null && t.Stats != null && IsSelfOrAncestor(l.Into, t.Stats)) return true;
                // Retargeted while suspended: clear its flag so it applies afresh to the
                // new target instead of believing it already has.
                if (_effectApplied != null) _effectApplied(act) = false;
                return false;
            }
            return true;
        }

        private static bool IsSelfOrAncestor(StatTagCollection a, StatTagCollection of)
        {
            for (StatTagCollection p = of; p != null; p = p.ParentCollection)
                if (ReferenceEquals(p, a)) return true;
            return false;
        }

        /// <summary>A volley in progress is a channel. Its weapon ticks its own pattern and
        /// sequence, so the weapon itself is held: one carrying an active volley ability,
        /// and a sniper's volley weapons while it is firing. Other weapons keep firing.</summary>
        private static void FreezeWeapons(Host h)
        {
            if (h.Ai == null) return;
            NpcShooter shooter = h.Obj.GetComponent<NpcShooter>();
            if (shooter == null || shooter.Weapons == null) return;
            foreach (BaseNpcAbility a in h.Ai.Abilities)
            {
                ActiveShootProjectilesAbility v = a as ActiveShootProjectilesAbility;
                if (v != null && v.IsActive) Hold(h, shooter, v.WeaponIndex);
            }
            if (h.Ai.Behaviours == null || _sniperAttack == null) return;
            foreach (BaseBehaviour b in h.Ai.Behaviours)
            {
                BehSniperAttack s = b as BehSniperAttack;
                if (s == null || !ReferenceEquals(h.Ai.ActiveBehaviour, s) ||
                    s.ActiveBehaviourStep != BehSniperAttack.SniperAttackStep.Attacking) continue;
                ActLimitedVolleyAttack volley = _sniperAttack(s);
                if (volley != null && volley.WeaponsToFireIndices != null)
                    foreach (int i in volley.WeaponsToFireIndices) Hold(h, shooter, i);
            }
        }

        private static void Hold(Host h, NpcShooter shooter, int index)
        {
            if (index < 0 || index >= shooter.Weapons.Count) return;
            NpcWeapon w = shooter.Weapons[index];
            if (w != null && FrozenWeapons.Add(w)) h.Weapons.Add(w);
        }

        // ---------------------------------------------------------------- markers

        /// <summary>The game's ability and call markers count their circle down on their
        /// own schedule. Paused here, and restarted at the end from the same point.</summary>
        private static void FreezeMarker(Host h)
        {
            // The provider also holds markers for systems that keep running, such as an
            // enemy charging a jump out of the sector. Freezing those would stall a circle
            // over something that is still counting down.
            if (!FrozenSystemOwnsMarker(h)) return;
            ObjectMarkerData data = AbilityMarkerOf(h.Obj);
            if (data == null) return;
            MarkerVE ve = data.Ve;
            if (ve == null) return;
            IVisualElementScheduledItem sched =
                Refl.GetField<IVisualElementScheduledItem>(ve, "abilitySchedule", null);
            if (sched == null || _abilityProgress == null) return;
            FrozenMarker m = new FrozenMarker
            {
                Data = data,
                Ve = ve,
                Progress = (float)_abilityProgress.GetValue(ve, null),
                AbilityTime = Refl.GetField<float>(ve, "abilityTime", 0f),
            };
            if (m.AbilityTime <= 0f) return;
            if (!Refl.SetFieldLogged(ve, "abilitySchedule", null)) return;
            sched.Pause();
            h.Marker = m;
        }

        private static void ThawMarker(Host h)
        {
            FrozenMarker m = h.Marker;
            h.Marker = null;
            if (m == null || h.Obj == null) return;
            if (!ReferenceEquals(AbilityMarkerOf(h.Obj), m.Data) || !ReferenceEquals(m.Data.Ve, m.Ve))
                return;   // the marker went or was rebuilt meanwhile; its new owner animates it
            float remaining = m.Data.AbilityDirectionInverted
                ? m.Progress * m.AbilityTime
                : (1f - m.Progress) * m.AbilityTime;
            if (remaining <= 1f) return;
            // The viewer restarts from AbilityChannelTimeMS, so it runs the remaining time;
            // progress and rate are then put back so the circle continues from where it was.
            m.Data.AbilityChannelTimeMS = (long)remaining;
            m.Ve.StartAbilityAnimation(m.Data);
            _abilityProgress.SetValue(m.Ve, m.Progress, null);
            Refl.SetField(m.Ve, "abilityTime", m.AbilityTime);
        }

        /// <summary>True when something this suspension holds is what the game is drawing
        /// a countdown for: an ability's cast or channel that shows UI feedback, or a call.</summary>
        private static bool FrozenSystemOwnsMarker(Host h)
        {
            if (h.Caller != null && _isCalling != null && _isCalling(h.Caller)) return true;
            if (h.Ai == null) return false;
            foreach (BaseNpcAbility a in h.Ai.Abilities)
            {
                BaseToggleableAbility t = a as BaseToggleableAbility;
                if (t == null || !t.IsActive || !t.ShowsUIFeedback) continue;
                if (t.IsCasting) return true;
                BaseDurationAbility d = t as BaseDurationAbility;
                if (d != null && d.HasChannelTime) return true;
            }
            if (h.Ai.Behaviours != null && _reinforceAction != null)
                foreach (BaseBehaviour b in h.Ai.Behaviours)
                {
                    BehSpawnReinforcements r = b as BehSpawnReinforcements;
                    ActReinforce act = r == null ? null : _reinforceAction(r);
                    if (act != null && CallInProgress(act)) return true;
                }
            return false;
        }

        private static ObjectMarkerData AbilityMarkerOf(OrbitObject obj)
        {
            ObjectMarkerRepository repo = ObjectMarkerRepository.Instance;
            if (repo == null || _providers == null) return null;
            Dictionary<Type, IMarkerProvider> providers = _providers(repo);
            IMarkerProvider p;
            if (providers == null ||
                !providers.TryGetValue(typeof(NpcAbilityMarkerProvider), out p)) return null;
            MarkerProvider<OrbitObject> mp = p as MarkerProvider<OrbitObject>;
            ObjectMarkerData data;
            return mp != null && mp.Markers != null && mp.Markers.TryGetValue(obj, out data)
                ? data : null;
        }

        // ---------------------------------------------------------------- replays

        private static void Replay()
        {
            List<Action> due = new List<Action>(DueReplays);
            DueReplays.Clear();
            for (int i = 0; i < due.Count; i++)
            {
                try { due[i](); }
                catch (Exception e)
                {
                    if (EnemyStatuses.WarnOnce("abilities:replay"))
                        Plugin.Log.LogError("Ectype: replaying a held ability start failed " +
                                            "(suppressing further reports): " + e);
                }
            }
        }

        private static void ReplayStart(BaseNpcAbility a)
        {
            OrbitObject owner = a.OrbitObject;
            if (owner == null || !owner.isActiveAndEnabled) return;
            // The normal path: cooldown and concurrency apply, and a suspension still
            // covering it holds it again.
            a.TryActivateAbility();
        }

        /// <summary>A shield cast the behaviour fires on a defend command rather than on
        /// its period. Replayed only while that behaviour is still the one running.</summary>
        private static void ReplayShield(ActShieldTargets cast)
        {
            NpcAI ai = cast.AI;
            if (ai == null || !ai.isActiveAndEnabled || ai.ActiveBehaviour == null) return;
            if (!(ai.ActiveBehaviour is BehShieldGenerateAOE) ||
                !ai.ActiveBehaviour.Actions.Contains(cast)) return;
            cast.Tick();
        }

        // ---------------------------------------------------------------- classification

        private static readonly HashSet<string> NotAbilities = new HashSet<string>
        {
            "BehAnchorAttackTarget", "BehAttack", "BehIntercept", "BehOrbitAttackTarget",
            "BehApproachTarget", "BehEscort", "BehFleeFromTarget", "BehFleeWhileTargetFinding",
            "BehIdle", "BehInvestigate", "BehJumpToSector", "BehMoveToPointWhileProtected",
            "BehOrbitAndRetaliate", "BehOrbitAroundTarget", "BehPatrolThroughWaypoints",
            "BehReachLastSignature", "BehReachPoint", "BehRoam", "BehRoamAroundPosition",
            "BehRoamWhileTargetFinding", "BehWaitForDuration", "BehSpawnOnDeath",
            // these drive the NPC abilities, which are described on their own
            "CastAbilitiesRandomlyPhaseDrivenBeh", "CastAbilitiesSequentiallyPhaseDrivenBeh",
        };

        private static void DescribeHost(OrbitObject host, List<EnemyAbilityInfo> into)
        {
            NpcAI ai = host.GetComponent<NpcAI>();
            if (ai != null)
            {
                foreach (BaseNpcAbility a in ai.Abilities)
                    if (a != null)
                        into.Add(new EnemyAbilityInfo(host, "npc-ability", AbilityName(a),
                                                      AbilityPhase(a)));
                if (ai.Behaviours != null)
                    foreach (BaseBehaviour b in ai.Behaviours)
                    {
                        if (b == null) continue;
                        EnemyAbilityInfo info = BehaviourInfo(host, ai, b);
                        if (info != null) into.Add(info);
                        else if (!NotAbilities.Contains(b.GetType().Name) &&
                                 EnemyStatuses.WarnOnce("abilities:unknown:" + b.GetType().Name))
                            Plugin.Log.LogWarning("Ectype: enemy behaviour " + b.GetType().Name +
                                                  " is not classified as an ability or as " +
                                                  "movement/attack; ability suspension does " +
                                                  "not reach it.");
                    }
            }
            NpcReinforcementCaller c = host.GetComponent<NpcReinforcementCaller>();
            if (c != null)
                into.Add(new EnemyAbilityInfo(host, "reinforcement-caller", "Reinforcement Call",
                                              CallerPhase(c)));
        }

        private static string AbilityName(BaseNpcAbility a)
        {
            try
            {
                if (a.ContextInfo != null && !string.IsNullOrEmpty(a.ContextInfo.HeaderText))
                    return a.ContextInfo.HeaderText;
            }
            catch { }
            return a.GetType().Name;
        }

        private static EnemyAbilityPhase AbilityPhase(BaseNpcAbility a)
        {
            if (a is BasePassiveAbility)
                return a.IsActive ? EnemyAbilityPhase.Sustained : EnemyAbilityPhase.Ready;
            BaseToggleableAbility t = a as BaseToggleableAbility;
            if (t == null) return a.IsActive ? EnemyAbilityPhase.Sustained : EnemyAbilityPhase.Ready;
            if (t.IsActive)
            {
                if (t.IsCasting) return EnemyAbilityPhase.Casting;
                BaseDurationAbility d = t as BaseDurationAbility;
                return d != null && d.HasChannelTime ? EnemyAbilityPhase.Channeling
                                                     : EnemyAbilityPhase.Sustained;
            }
            return t.IsOnCooldown ? EnemyAbilityPhase.Cooldown : EnemyAbilityPhase.Ready;
        }

        private static EnemyAbilityInfo BehaviourInfo(OrbitObject host, NpcAI ai, BaseBehaviour b)
        {
            bool running = ReferenceEquals(ai.ActiveBehaviour, b);
            BehSniperAttack sniper = b as BehSniperAttack;
            if (sniper != null)
            {
                EnemyAbilityPhase p = EnemyAbilityPhase.Ready;
                if (running)
                    switch (sniper.ActiveBehaviourStep)
                    {
                        case BehSniperAttack.SniperAttackStep.Warmup:
                        case BehSniperAttack.SniperAttackStep.LockingOn: p = EnemyAbilityPhase.Casting; break;
                        case BehSniperAttack.SniperAttackStep.Attacking: p = EnemyAbilityPhase.Channeling; break;
                        case BehSniperAttack.SniperAttackStep.Cooldown: p = EnemyAbilityPhase.Cooldown; break;
                    }
                return new EnemyAbilityInfo(host, "sniper-cycle", "Lock-on Volley", p);
            }
            BehDebuffApplier latch = b as BehDebuffApplier;
            if (latch != null)
            {
                EnemyAbilityPhase p = EnemyAbilityPhase.Ready;
                if (running)
                {
                    int step = LatchStep(latch);
                    p = step == 1 ? EnemyAbilityPhase.Sustained
                      : step == 2 ? EnemyAbilityPhase.Cooldown : EnemyAbilityPhase.Ready;
                }
                return new EnemyAbilityInfo(host, "debuff-latch", "Debuff Latch", p);
            }
            if (b is BehStatModifyWhileOrbiting)
            {
                ActApplyStatMod act = FirstStatAction(b);
                return new EnemyAbilityInfo(host, "stat-aura", "Ally Buff",
                    act != null && act.EffectApplied ? EnemyAbilityPhase.Sustained
                                                     : EnemyAbilityPhase.Ready);
            }
            if (b is BehShieldGenerateAOE)
                return new EnemyAbilityInfo(host, "shield-pulse", "Shield Pulse",
                    running ? EnemyAbilityPhase.Cooldown : EnemyAbilityPhase.Ready);
            BehSpawnReinforcements beh = b as BehSpawnReinforcements;
            if (beh != null)
            {
                ActReinforce act = _reinforceAction == null ? null : _reinforceAction(beh);
                return new EnemyAbilityInfo(host, "reinforcement-behaviour", "Reinforcement Call",
                    act != null && CallInProgress(act) ? EnemyAbilityPhase.Channeling
                                                       : EnemyAbilityPhase.Ready);
            }
            return null;
        }

        private static EnemyAbilityPhase CallerPhase(NpcReinforcementCaller c)
        {
            if (_isCalling != null && _isCalling(c)) return EnemyAbilityPhase.Channeling;
            if (_hasCalled != null && _hasCalled(c)) return EnemyAbilityPhase.Spent;
            return EnemyAbilityPhase.Ready;
        }

        private static ActApplyStatMod FirstStatAction(BaseBehaviour b)
        {
            if (b.Actions == null) return null;
            foreach (BaseAction a in b.Actions)
            {
                ActApplyStatMod m = a as ActApplyStatMod;
                if (m != null) return m;
            }
            return null;
        }

        /// <summary>0 approaching, 1 latched, 2 orbiting; -1 when unreadable.</summary>
        private static int LatchStep(BehDebuffApplier latch)
        {
            if (_latchStep == null) return -1;
            try { return Convert.ToInt32(_latchStep.GetValue(latch)); }
            catch { return -1; }
        }

        // ---------------------------------------------------------------- gates

        private static bool AbilityStartPrefix(BaseNpcAbility __instance, bool __0, ref bool __result,
                                               out bool __state)
        {
            __state = true;
            try
            {
                __state = __instance.IsActive;
                if (__0 || Hosts.Count == 0) return true;
                Host h;
                OrbitObject o = __instance.OrbitObject;
                if (ReferenceEquals(o, null) || !Hosts.TryGetValue(o, out h)) return true;
                if (_retryingCaller == 0 && !__instance.IsActive && !h.Deferred.Contains(__instance))
                    h.Deferred.Add(__instance);
                Report(h, REPORT_ABILITY, __instance.GetType().Name);
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                GateFailed("ability start", e);
                return true;
            }
        }

        /// <summary>
        /// A forced start on a suspended host is a replica copying a start its controller
        /// made before the suspension reached it (the stream can trail the status). The
        /// suspension lifted everything else at its start; this one's effects were applied
        /// just now, so they are lifted the same way, and any record from an earlier run of
        /// the same ability is dropped, since its deactivation already released those.
        /// </summary>
        private static void AbilityStartPostfix(BaseNpcAbility __instance, bool __0, bool __state)
        {
            try
            {
                if (!__0 || __state || Hosts.Count == 0 || !__instance.IsActive) return;
                Host h;
                OrbitObject o = __instance.OrbitObject;
                if (ReferenceEquals(o, null) || !Hosts.TryGetValue(o, out h)) return;
                h.LiftedSets.RemoveAll(l => ReferenceEquals(l.Source, __instance));
                HashSet<object> one = new HashSet<object>(ByRef<object>.Instance);
                one.Add(__instance);
                LiftSources(h, one);
                h.ActiveAtStart[__instance] = true;
                ActiveShootProjectilesAbility v = __instance as ActiveShootProjectilesAbility;
                NpcShooter shooter = v == null ? null : h.Obj.GetComponent<NpcShooter>();
                if (shooter != null && shooter.Weapons != null) Hold(h, shooter, v.WeaponIndex);
            }
            catch (Exception e) { GateFailed("forced start", e); }
        }

        private static bool AbilityTickPrefix(BaseNpcAbility __instance)
        {
            try { return Hosts.Count == 0 || !Frozen(__instance.OrbitObject); }
            catch (Exception e) { GateFailed("ability tick", e); return true; }
        }

        private static void RetryingEnter() { _retryingCaller++; }

        private static void RetryingExit() { if (_retryingCaller > 0) _retryingCaller--; }

        private static bool SniperTickPrefix(BehSniperAttack __instance)
        {
            try
            {
                if (Hosts.Count == 0 || !Frozen(AiOf(__instance))) return true;
                // Repositioning is movement and goes on; the step into warm-up waits.
                if (__instance.ActiveBehaviourStep == BehSniperAttack.SniperAttackStep.Positioning &&
                    _sniperMove != null)
                {
                    ActMoveRotateTowards move = _sniperMove(__instance);
                    if (move != null) move.Tick();
                }
                return false;
            }
            catch (Exception e) { GateFailed("sniper cycle", e); return true; }
        }

        private static bool LatchTickPrefix(BehDebuffApplier __instance)
        {
            try
            {
                if (Hosts.Count == 0 || !Frozen(AiOf(__instance))) return true;
                // Following the target is movement and goes on; latching on, the latch's
                // effect and the orbit wait are held.
                if (LatchStep(__instance) == 2)
                {
                    ActOrbitTarget orbit = _latchOrbit == null ? null : _latchOrbit(__instance);
                    if (orbit != null) orbit.Tick();
                }
                else
                {
                    ActAnchorTarget anchor = _latchAnchor == null ? null : _latchAnchor(__instance);
                    if (anchor != null) anchor.Tick();
                }
                return false;
            }
            catch (Exception e) { GateFailed("debuff latch", e); return true; }
        }

        private static bool StatActionTickPrefix(ActApplyStatMod __instance)
        {
            try { return Hosts.Count == 0 || __instance.AI == null || !Frozen(__instance.AI.OrbitObject); }
            catch (Exception e) { GateFailed("stat action", e); return true; }
        }

        private static bool ShieldTickPrefix(BehShieldGenerateAOE __instance)
        {
            try { return Hosts.Count == 0 || !Frozen(AiOf(__instance)); }
            catch (Exception e) { GateFailed("shield pulse", e); return true; }
        }

        private static bool ShieldCastPrefix(ActShieldTargets __instance)
        {
            try
            {
                if (Hosts.Count == 0 || __instance.AI == null) return true;
                Host h;
                OrbitObject o = __instance.AI.OrbitObject;
                if (ReferenceEquals(o, null) || !Hosts.TryGetValue(o, out h)) return true;
                // Reached from a defend command, which fires once (the behaviour's period is
                // skipped while held), so it is replayed at the end.
                if (!h.DeferredShields.Contains(__instance)) h.DeferredShields.Add(__instance);
                return false;
            }
            catch (Exception e) { GateFailed("shield cast", e); return true; }
        }

        private static bool ReinforceTickPrefix(ActReinforce __instance)
        {
            try
            {
                if (Hosts.Count == 0 || __instance.AI == null) return true;
                Host h;
                OrbitObject o = __instance.AI.OrbitObject;
                if (ReferenceEquals(o, null) || !Hosts.TryGetValue(o, out h)) return true;
                Report(h, REPORT_BEHAVIOUR, "reinforcement behaviour");
                return false;
            }
            catch (Exception e) { GateFailed("reinforcement behaviour", e); return true; }
        }

        private static bool CallerUpdatePrefix(NpcReinforcementCaller __instance)
        {
            try
            {
                Host h;
                OrbitObject o = Hosts.Count == 0 ? null : __instance.OrbitObject;
                if (ReferenceEquals(o, null) || !Hosts.TryGetValue(o, out h)) return true;
                Report(h, REPORT_CALLER, "reinforcement call");
                return false;
            }
            catch (Exception e) { GateFailed("reinforcement caller", e); return true; }
        }

        private static bool WeaponTickPrefix(NpcWeapon __instance)
        {
            return FrozenWeapons.Count == 0 || !FrozenWeapons.Contains(__instance);
        }

        private static OrbitObject AiOf(BaseBehaviour b)
        {
            NpcAI ai = _behaviourAi == null ? null : _behaviourAi(b);
            return ai == null ? null : ai.OrbitObject;
        }

        private static void Report(Host h, int kind, string what)
        {
            if (!Plugin.Verbose || (h.Reported & kind) != 0) return;
            h.Reported |= kind;
            Plugin.Log.LogInfo("Ectype: held a " + what + " on suspended " +
                               (h.Obj != null ? h.Obj.name : "?") + ".");
        }

        private static void GateFailed(string gate, Exception e)
        {
            if (EnemyStatuses.WarnOnce("abilities:gate:" + gate))
                Plugin.Log.LogError("Ectype: the " + gate + " gate threw and let the game run " +
                                    "(suppressing further reports): " + e);
        }

        // ---------------------------------------------------------------- patching

        private static AccessTools.FieldRef<BaseToggleableAbility, int> _lastUsage;
        private static AccessTools.FieldRef<ActTimer, int> _timerStart;
        private static AccessTools.FieldRef<BaseBehaviour, NpcAI> _behaviourAi;
        private static AccessTools.FieldRef<BehSniperAttack, ActMoveRotateTowards> _sniperMove;
        private static AccessTools.FieldRef<BehSniperAttack, ActTimedIdle> _sniperPreIdle;
        private static AccessTools.FieldRef<BehSniperAttack, ActTimedIdle> _sniperPostIdle;
        private static AccessTools.FieldRef<BehSniperAttack, ActLimitedVolleyAttack> _sniperAttack;
        private static AccessTools.FieldRef<BehDebuffApplier, ActAnchorTarget> _latchAnchor;
        private static AccessTools.FieldRef<BehDebuffApplier, ActOrbitTarget> _latchOrbit;
        private static AccessTools.FieldRef<BehDebuffApplier, ActTimer> _latchTimer;
        private static FieldInfo _latchStep;
        private static AccessTools.FieldRef<BehSpawnReinforcements, ActReinforce> _reinforceAction;
        private static AccessTools.FieldRef<ActReinforce, int> _reinforceStart;
        private static AccessTools.FieldRef<ActReinforce, SpawnerUnitEntry> _unitsReinforcing;
        private static AccessTools.FieldRef<NpcReinforcementCaller, bool> _isCalling;
        private static AccessTools.FieldRef<NpcReinforcementCaller, bool> _hasCalled;
        private static AccessTools.FieldRef<NpcReinforcementCaller, int> _callStart;
        private static AccessTools.FieldRef<ActApplyStatMod, bool> _effectApplied;
        private static AccessTools.FieldRef<ObjectMarkerRepository, Dictionary<Type, IMarkerProvider>> _providers;
        private static PropertyInfo _abilityProgress;

        private static readonly List<string> Misses = new List<string>();

        /// <summary>
        /// Applied after the framework's attribute patches, one gate at a time: a game update
        /// that renames a member costs the system that needs it and is named in the log,
        /// instead of aborting the framework's patch pass.
        /// </summary>
        internal static void ApplyPatches(Harmony harmony)
        {
            _lastUsage = Ref<BaseToggleableAbility, int>("LastUsageTimestamp");
            _timerStart = Ref<ActTimer, int>("idleStartTime");
            _behaviourAi = Ref<BaseBehaviour, NpcAI>("ai");
            _sniperMove = Ref<BehSniperAttack, ActMoveRotateTowards>("moveAndRotateToTargetAction");
            _sniperPreIdle = Ref<BehSniperAttack, ActTimedIdle>("preIdleAction");
            _sniperPostIdle = Ref<BehSniperAttack, ActTimedIdle>("postIdleAction");
            _sniperAttack = Ref<BehSniperAttack, ActLimitedVolleyAttack>("attackAction");
            _latchAnchor = Ref<BehDebuffApplier, ActAnchorTarget>("anchorAction");
            _latchOrbit = Ref<BehDebuffApplier, ActOrbitTarget>("orbitAction");
            _latchTimer = Ref<BehDebuffApplier, ActTimer>("timerAction");
            _latchStep = Refl.Field(typeof(BehDebuffApplier), "activeStep");
            if (_latchStep == null) Misses.Add("BehDebuffApplier.activeStep");
            _reinforceAction = Ref<BehSpawnReinforcements, ActReinforce>("reinforceAction");
            _reinforceStart = Ref<ActReinforce, int>("reinforceStartTimestamp");
            _unitsReinforcing = Ref<ActReinforce, SpawnerUnitEntry>("unitsReinforcing");
            _isCalling = Ref<NpcReinforcementCaller, bool>("_isCalling");
            _hasCalled = Ref<NpcReinforcementCaller, bool>("_hasSuccessfullyCalled");
            _callStart = Ref<NpcReinforcementCaller, int>("_callStartTimeInMilliseconds");
            _effectApplied = Ref<ActApplyStatMod, bool>("effectApplied");
            _providers = Ref<ObjectMarkerRepository, Dictionary<Type, IMarkerProvider>>("providers");
            _abilityProgress = typeof(MarkerVE).GetProperty("AbilityProgress",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_abilityProgress == null) Misses.Add("MarkerVE.AbilityProgress");

            int patched = 0;
            patched += PatchNamed(harmony, typeof(BaseNpcAbility), "TryActivateAbility",
                             new[] { typeof(bool) }, "AbilityStartPrefix", null,
                             "AbilityStartPostfix");
            patched += PatchAbilityTicks(harmony);
            patched += PatchNamed(harmony, typeof(CastAbilitiesRandomlyPhaseDrivenBeh), "Tick",
                             Type.EmptyTypes, "RetryingEnter", "RetryingExit");
            patched += PatchNamed(harmony, typeof(CastAbilitiesSequentiallyPhaseDrivenBeh), "Tick",
                             Type.EmptyTypes, "RetryingEnter", "RetryingExit");
            patched += PatchNamed(harmony, typeof(BehSniperAttack), "Tick", Type.EmptyTypes,
                             "SniperTickPrefix", null);
            patched += PatchNamed(harmony, typeof(BehDebuffApplier), "Tick", Type.EmptyTypes,
                             "LatchTickPrefix", null);
            patched += PatchNamed(harmony, typeof(ActApplyStatMod), "Tick", Type.EmptyTypes,
                             "StatActionTickPrefix", null);
            patched += PatchNamed(harmony, typeof(BehShieldGenerateAOE), "Tick", Type.EmptyTypes,
                             "ShieldTickPrefix", null);
            patched += PatchNamed(harmony, typeof(ActShieldTargets), "Tick", Type.EmptyTypes,
                             "ShieldCastPrefix", null);
            patched += PatchNamed(harmony, typeof(ActReinforce), "Tick", Type.EmptyTypes,
                             "ReinforceTickPrefix", null);
            patched += PatchNamed(harmony, typeof(NpcReinforcementCaller), "Update", Type.EmptyTypes,
                             "CallerUpdatePrefix", null);
            patched += PatchNamed(harmony, typeof(NpcWeapon), "Tick", new[] { typeof(float) },
                             "WeaponTickPrefix", null);

            if (Misses.Count == 0)
                Plugin.Log.LogInfo("Ectype: enemy ability gates applied (" + patched + " methods).");
            else
                Plugin.Log.LogWarning("Ectype: enemy ability gates applied (" + patched +
                                      " methods) with " + Misses.Count + " member(s) missing, " +
                                      "so suspension does not fully reach the systems that use " +
                                      "them: " + string.Join(", ", Misses.ToArray()) +
                                      ". A game update likely renamed them.");
        }

        /// <summary>Every declared Tick and PassiveTick in the ability hierarchy, so a
        /// subclass the game adds later is held like the rest.</summary>
        private static int PatchAbilityTicks(Harmony harmony)
        {
            int n = 0;
            Type[] types;
            try { types = typeof(BaseNpcAbility).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (Type t in types)
            {
                if (t == null || !typeof(BaseNpcAbility).IsAssignableFrom(t)) continue;
                foreach (string name in new[] { "Tick", "PassiveTick" })
                {
                    MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                               null, Type.EmptyTypes, null);
                    if (m == null || m.IsAbstract) continue;
                    n += PatchMethod(harmony, m, "AbilityTickPrefix", null);
                }
            }
            return n;
        }

        private static int PatchNamed(Harmony harmony, Type type, string name, Type[] args,
                                      string prefix, string finalizer, string postfix = null)
        {
            MethodInfo m = AccessTools.Method(type, name, args);
            if (m == null)
            {
                Misses.Add(type.Name + "." + name);
                return 0;
            }
            return PatchMethod(harmony, m, prefix, finalizer, postfix);
        }

        private static int PatchMethod(Harmony harmony, MethodInfo m, string prefix,
                                       string finalizer, string postfix = null)
        {
            try
            {
                harmony.Patch(m,
                    prefix: new HarmonyMethod(Own(prefix)),
                    postfix: postfix == null ? null : new HarmonyMethod(Own(postfix)),
                    finalizer: finalizer == null ? null : new HarmonyMethod(Own(finalizer)));
                return 1;
            }
            catch (Exception e)
            {
                Misses.Add(m.DeclaringType.Name + "." + m.Name);
                Plugin.Log.LogError("Ectype: patching " + m.DeclaringType.Name + "." + m.Name +
                                    " for ability suspension failed: " + e);
                return 0;
            }
        }

        private static MethodInfo Own(string name)
        {
            return typeof(EnemyAbilities).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static AccessTools.FieldRef<T, F> Ref<T, F>(string field) where T : class
        {
            try { return AccessTools.FieldRefAccess<T, F>(field); }
            catch
            {
                Misses.Add(typeof(T).Name + "." + field);
                return null;
            }
        }

        /// <summary>Unity objects compare by native identity and a destroyed one equals
        /// null; these tables want the managed reference.</summary>
        private sealed class ByRef<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ByRef<T> Instance = new ByRef<T>();
            public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
            public int GetHashCode(T o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
