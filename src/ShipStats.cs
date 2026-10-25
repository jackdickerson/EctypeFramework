using System;
using System.Collections.Generic;

using CG.Game;
using CG.Space;
using CG.Space.Projectiles;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Framework stats that live on each player ship's own collection.
    ///
    /// IncomingProjectileSpeed is a factor (authored 1) on the speed of hostile NPC projectiles.
    /// It has no field to sink into: each ship owns its number, and each enemy shot carries its
    /// own Speed stat, so the consumer puts one modifier of (factor - 1) on a hostile shot as it
    /// registers and re-applies it to that ship's shots in flight whenever the ship's factor
    /// changes. A shot reads Speed.Value every tick for movement and for its Range / Speed
    /// lifetime, so a slowed shot slows at once and still reaches its full range.
    ///
    /// Every client simulates every NPC shot and damage lands only on the hit object's owner, so
    /// each shot must carry the same factor on every client. Two rules make it so. The ship whose
    /// factor a shot takes is chosen once, at registration, from data every client shares: the
    /// guided target's ship when the shot is guided at a player ship, else the living player ship
    /// hostile to the shot with the smallest angle between the shot's direction and the line to
    /// the ship, ties to the nearest. Each ship's factor is the same on every client, because a
    /// seatShip perk's mods are applied to the seat's own ship on every client for every seated
    /// player, remote replicas included, from the perk levels each player publishes. With one ship
    /// in the room the choice is that ship, as it always was.
    ///
    /// A shot whose source is a player ship is skipped: a multi-ship mod can route player missiles
    /// through the NPC projectile path, and this stat is about enemy fire.
    /// </summary>
    internal static class ShipStats
    {
        /// <summary>Removal key for the per-shot modifier; each shot's Speed holds at most one.</summary>
        private sealed class SlowSource : IModifierSource
        {
            public override string ToString() { return "Ectype incoming projectile speed"; }
        }

        /// <summary>The ship a shot was bound to at registration.</summary>
        private struct Binding
        {
            public SyncedProjectile Shot;
            public PlayerControlledShip Ship;
        }

        private static readonly SlowSource Source = new SlowSource();

        // A zero speed divides by zero in the shot's lifetime; keep a sliver of motion.
        private const float MinFactor = 0.05f;
        // Shots within this many degrees of each other's best line count as a tie.
        private const float AngleTieDegrees = 0.5f;
        private const int CachePruneAt = 512;

        private static readonly Dictionary<int, Binding> Bound = new Dictionary<int, Binding>();
        private static readonly Dictionary<PlayerControlledShip, float> LastLogged =
            new Dictionary<PlayerControlledShip, float>();
        private static bool _loggedWiring;

        public static void Wire(PlayerControlledShip ship)
        {
            try
            {
                if (ship == null || ship.Stats == null || ship.Stats.Stats == null) return;
                FrameworkStats.EnsureRegistered();
                int id = FrameworkStats.Id(FrameworkStats.IncomingProjectileSpeed);
                if (id == 0 || ship.Stats.Stats.ContainsKey(id)) return;

                ModifiableFloat stat = (ModifiableFloat)1f;
                // Subscribe BEFORE registering: RegisterStat replays modifiers already on the
                // collection, and each replay raises the event. One subscription per ship, so a
                // change re-applies that ship's shots only.
                stat.OnValueChange += () => OnFactorChanged(ship);
                ship.Stats.RegisterStat(id, ref stat);
                string line = "Ectype: ship stat surface installed (" + FrameworkStats.IncomingProjectileSpeed +
                              "=" + stat.Value.ToString("0.###") + ").";
                if (!_loggedWiring) { _loggedWiring = true; Plugin.Log.LogInfo(line); }
                else if (Plugin.Verbose) Plugin.Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: wiring ship stats failed - enemy projectiles " +
                                    "keep their authored speed: " + e);
            }
        }

        /// <summary>The ship's current factor, or 1 when the stat is not wired on it.</summary>
        private static float Factor(PlayerControlledShip ship)
        {
            if (ship == null || ship.Stats == null || ship.Stats.Stats == null) return 1f;
            int id = FrameworkStats.Id(FrameworkStats.IncomingProjectileSpeed);
            StatBase sb;
            if (id == 0 || !ship.Stats.Stats.TryGetValue(id, out sb)) return 1f;
            ModifiableFloat f = sb.Data as ModifiableFloat;
            return f == null ? 1f : Mathf.Max(MinFactor, f.Value);
        }

        /// <summary>A shot as it registers: choose its ship once, then apply that ship's factor.</summary>
        public static void Register(SyncedProjectile p)
        {
            try
            {
                if (p == null || p.Speed == null) return;
                if (p.Source is PlayerControlledShip) return;
                PlayerControlledShip ship = ChooseShip(p);
                if (ship == null) return;

                if (Bound.Count >= CachePruneAt) Prune();
                Bound[p.ProjectileId] = new Binding { Shot = p, Ship = ship };
                float factor = ApplyFactor(p, ship);
                if (Plugin.Verbose && (Topology.Multi || Math.Abs(factor - 1f) > 0.0001f))
                    Plugin.Log.LogInfo("Ectype: " + FrameworkStats.IncomingProjectileSpeed + ": shot " +
                                       p.ProjectileId + " → ship " + Topology.ShipNumberOf(ship) +
                                       ", factor " + factor.ToString("0.###") + ".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: applying incoming projectile speed failed: " + e);
            }
        }

        /// <summary>
        /// The ship whose factor this shot takes, or null when no player ship is hostile to it.
        /// One ship object: that ship. Several: the guided target's ship, else the hostile ship
        /// nearest the shot's line.
        /// </summary>
        private static PlayerControlledShip ChooseShip(SyncedProjectile p)
        {
            if (!Topology.Multi)
            {
                PlayerControlledShip only = ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
                return only != null && only.IsObjectFromEnemyFaction(p.Faction) ? only : null;
            }

            GuidedProjectile guided = p as GuidedProjectile;
            if (guided != null && guided.Target != null)
            {
                PlayerControlledShip target = Topology.ShipOfPart(guided.Target);
                if (target != null && !target.StartedDeath && target.IsObjectFromEnemyFaction(p.Faction))
                    return target;
            }

            Vector3 from = p.transform.position;
            Vector3 dir = p.Velocity.sqrMagnitude > 0f ? p.Velocity.normalized : p.transform.forward;
            PlayerControlledShip best = null;
            float bestAngle = float.MaxValue, bestDistance = float.MaxValue;
            IReadOnlyList<PlayerControlledShip> ships = Topology.Ships;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip s = ships[i];
                if (s == null || !s.IsObjectFromEnemyFaction(p.Faction)) continue;
                Vector3 to = s.transform.position - from;
                float distance = to.magnitude;
                float angle = distance > 0f ? Vector3.Angle(dir, to) : 0f;
                bool better = angle < bestAngle - AngleTieDegrees ||
                              (angle <= bestAngle + AngleTieDegrees && distance < bestDistance);
                if (!better) continue;
                best = s;
                bestAngle = angle;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>Puts the ship's factor on the shot, replacing any earlier one. Returns the
        /// factor.</summary>
        private static float ApplyFactor(SyncedProjectile p, PlayerControlledShip ship)
        {
            float factor = Factor(ship);
            p.Speed.RemoveModsFromSource(Source);
            if (Math.Abs(factor - 1f) > 0.0001f)
            {
                FloatModifier mod = new FloatModifier(factor - 1f, ModifierType.ExponentialMultiplier);
                mod.Source = Source;
                p.Speed.AddModifier(mod, true);
            }
            // Velocity is a spawn-time snapshot the collision sweep sizes its ray from;
            // left alone, a slowed shot looks further ahead than it moves and hits early.
            if (p.Velocity.sqrMagnitude > 0f)
                p.Velocity = p.Velocity.normalized * p.Speed.Value;
            return factor;
        }

        /// <summary>One ship's factor changed: its shots in flight take the new value. The ship
        /// each shot is bound to stays the one chosen at registration.</summary>
        private static void OnFactorChanged(PlayerControlledShip ship)
        {
            try
            {
                float v = Factor(ship);
                float last;
                if (!LastLogged.TryGetValue(ship, out last) || Math.Abs(v - last) > 0.0001f)
                {
                    LastLogged[ship] = v;
                    if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: " + FrameworkStats.IncomingProjectileSpeed +
                                                           " on ship " + Topology.ShipNumberOf(ship) + " now " +
                                                           v.ToString("0.###") + ".");
                }

                SyncedProjectilesSynchronizer sync = SyncedProjectilesSynchronizer.Instance;
                if (sync == null) return;
                foreach (SyncedProjectile p in sync.Projectiles)
                {
                    if (p == null || p.Speed == null) continue;
                    Binding b;
                    if (!Bound.TryGetValue(p.ProjectileId, out b) || !ReferenceEquals(b.Shot, p)) continue;
                    if (!ReferenceEquals(b.Ship, ship)) continue;
                    ApplyFactor(p, ship);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: re-applying incoming projectile speed failed: " + e);
            }
        }

        /// <summary>Drops bindings whose shot is gone. The synchronizer removes shots through several
        /// paths, so the cache is pruned by size rather than hooked into each.</summary>
        private static void Prune()
        {
            List<int> dead = new List<int>();
            foreach (KeyValuePair<int, Binding> kv in Bound)
                if (kv.Value.Shot == null || kv.Value.Ship == null) dead.Add(kv.Key);
            foreach (int id in dead) Bound.Remove(id);
            List<PlayerControlledShip> gone = new List<PlayerControlledShip>();
            foreach (PlayerControlledShip s in LastLogged.Keys) if (s == null) gone.Add(s);
            foreach (PlayerControlledShip s in gone) LastLogged.Remove(s);
        }
    }

    /// <summary>
    /// Postfix: RegisterStats is where the ship fills its collection, on every replica.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControlledShip), "RegisterStats")]
    internal static class PlayerControlledShip_RegisterStats_Patch
    {
        private static void Postfix(PlayerControlledShip __instance)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            ShipStats.Wire(__instance);
            ShipPlatformStats.Wire(__instance);
        }
    }

    /// <summary>
    /// Every NPC shot spawn path - fresh, multi-shot and a late joiner's catch-up - ends here,
    /// after the shooter has set the shot's Speed, Faction and, for a guided shot, its target.
    /// </summary>
    [HarmonyPatch(typeof(SyncedProjectilesSynchronizer), "RegisterNpcProjectile")]
    internal static class SyncedProjectilesSynchronizer_RegisterNpcProjectile_Patch
    {
        private static void Postfix(SyncedProjectile __0)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            ShipStats.Register(__0);
        }
    }
}
