using System;

using CG.Space;
using Gameplay.SpacePlatforms;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// ShipDrag: the player ship platform's MovingSpacePlatform.PhysicalData.Friction, a raw field,
    /// made a FieldStat on the ship's own collection.
    ///
    /// ApplyFriction reads it every FixedUpdate on every machine, with no ownership gate, and
    /// moves the velocity toward zero by speed^2 * Friction * dt / Mass. No IL in the assembly
    /// stores to PhysicalData, and the only store to Friction is the struct's own constructor, so
    /// seeding from the field at RegisterStats is sound and the write-back is the field's only
    /// writer. docs/framework-stats.json has the other readers.
    ///
    /// Stat changes are not replicated. The helm owner simulates the platform and every other
    /// machine extrapolates with its own friction between snapshots, so a contribution belongs on
    /// every machine.
    /// </summary>
    internal static class ShipPlatformStats
    {
        // A zero drag never stops the ship and divides by zero in ShipPilotHelper.MaxVelocity.
        // 1% of authored caps top speed at 10x.
        private const float MinFactor = 0.01f;

        private static bool _loggedWiring;

        /// <summary>Runs inside the RegisterStats postfix. OrbitObject.Awake calls RegisterStats
        /// before PlayerControlledShip.Awake assigns Platform, so the platform is read from the
        /// ship's own GameObject, where that Awake finds it too.</summary>
        public static void Wire(PlayerControlledShip ship)
        {
            try
            {
                if (ship == null || ship.Stats == null) return;
                MovingSpacePlatform platform = ship.GetComponent<MovingSpacePlatform>();
                if (platform == null) return;

                float authored = platform.PhysicalData.Friction;
                float floor = authored * MinFactor;
                FieldStat fs = FieldStat.Wire(ship.Stats, FrameworkStats.ShipDrag,
                                              "ship " + FrameworkStats.ShipDrag,
                                              () => platform.PhysicalData.Friction,
                                              v => platform.PhysicalData.Friction = Mathf.Max(floor, v));
                if (fs == null || _loggedWiring) return;
                _loggedWiring = true;
                Plugin.Log.LogInfo("Ectype: ship drag stat installed (" + FrameworkStats.ShipDrag + "=" +
                                   fs.Authored.ToString("0.###") + ", mass " +
                                   platform.PhysicalData.Mass.ToString("0.###") + ").");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: wiring " + FrameworkStats.ShipDrag + " failed - the ship " +
                                    "keeps its authored drag: " + e);
            }
        }
    }
}
