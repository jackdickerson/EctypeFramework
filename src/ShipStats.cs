using System;

using CG.Game;
using CG.Space;
using CG.Space.Projectiles;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Framework stats that live on the player ship's own collection.
    ///
    /// IncomingProjectileSpeed is a factor (authored 1) on the speed of every hostile NPC
    /// projectile. It has no field to sink into: the ship owns the number, and each enemy shot
    /// carries its own Speed stat, so the consumer puts one modifier of (factor - 1) on every
    /// hostile shot as it registers and re-applies it to shots in flight whenever the factor
    /// changes. A shot reads Speed.Value every tick for movement and for its Range / Speed
    /// lifetime, so a slowed shot slows at once and still reaches its full range.
    ///
    /// Every client simulates every NPC shot and damage lands only on the hit object's
    /// owner, so the factor must be the same on every client. It is: a seatShip perk's mods
    /// are applied to each client's own PlayerShip.Stats for every seated player, remote
    /// replicas included, from the perk levels each player publishes.
    /// </summary>
    internal static class ShipStats
    {
        /// <summary>Removal key for the per-shot modifier; each shot's Speed holds at most one.</summary>
        private sealed class SlowSource : IModifierSource
        {
            public override string ToString() { return "Ectype incoming projectile speed"; }
        }

        private static readonly SlowSource Source = new SlowSource();

        // A zero speed divides by zero in the shot's lifetime; keep a sliver of motion.
        private const float MinFactor = 0.05f;

        private static ModifiableFloat _factor;
        private static float _lastLogged = 1f;

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
                // collection, and each replay raises the event.
                stat.OnValueChange += OnFactorChanged;
                ship.Stats.RegisterStat(id, ref stat);
                _factor = stat;
                Plugin.Log.LogInfo("Ectype: ship stat surface installed (" +
                                   FrameworkStats.IncomingProjectileSpeed + "=" +
                                   stat.Value.ToString("0.###") + ").");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: wiring ship stats failed - enemy projectiles " +
                                    "keep their authored speed: " + e);
            }
        }

        /// <summary>
        /// The current factor for the local player ship, or 1 when the stat is not wired to
        /// the ship this client is on.
        /// </summary>
        private static float Factor()
        {
            PlayerControlledShip ship = ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
            if (ship == null || ship.Stats == null) return 1f;
            int id = FrameworkStats.Id(FrameworkStats.IncomingProjectileSpeed);
            StatBase sb;
            if (id == 0 || !ship.Stats.Stats.TryGetValue(id, out sb)) return 1f;
            ModifiableFloat f = sb.Data as ModifiableFloat;
            return f == null ? 1f : Mathf.Max(MinFactor, f.Value);
        }

        public static void Apply(SyncedProjectile p)
        {
            try
            {
                if (p == null || p.Speed == null) return;
                PlayerControlledShip ship = ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
                if (ship == null || !ship.IsObjectFromEnemyFaction(p.Faction)) return;

                float factor = Factor();
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
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: applying incoming projectile speed failed: " + e);
            }
        }

        private static void OnFactorChanged()
        {
            try
            {
                float v = _factor != null ? _factor.Value : 1f;
                if (Math.Abs(v - _lastLogged) > 0.0001f)
                {
                    _lastLogged = v;
                    if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: " + FrameworkStats.IncomingProjectileSpeed +
                                                           " now " + v.ToString("0.###") + ".");
                }

                SyncedProjectilesSynchronizer sync = SyncedProjectilesSynchronizer.Instance;
                if (sync == null) return;
                foreach (SyncedProjectile p in sync.Projectiles) Apply(p);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: re-applying incoming projectile speed failed: " + e);
            }
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
    /// after the shooter has set the shot's Speed and Faction.
    /// </summary>
    [HarmonyPatch(typeof(SyncedProjectilesSynchronizer), "RegisterNpcProjectile")]
    internal static class SyncedProjectilesSynchronizer_RegisterNpcProjectile_Patch
    {
        private static void Postfix(SyncedProjectile __0)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            ShipStats.Apply(__0);
        }
    }
}
