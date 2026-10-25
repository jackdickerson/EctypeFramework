using System;

using CG.Space;
using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// A top-speed surge on the player ship that decays linearly to nothing, expressed as a cut to
    /// ShipDrag through a TimedStatContribution. One surge per ship: starting another replaces it.
    /// It ends early when the ship leaves normal flight, when the caller's keep-alive turns false,
    /// on End, at session end, and when the ship is destroyed.
    ///
    /// Run it on every machine from the same replicated signal, ShipArrivals.Arrived being the
    /// intended one. ShipDrag is read by every machine's platform simulation and stat changes are
    /// not replicated: the helm owner's simulation is authoritative and the rest extrapolate with
    /// their own drag. A machine that joined after the signal carries no surge; if it then takes the
    /// helm, the authoritative simulation has none and the surge ends early for the whole room.
    /// </summary>
    public static class ShipSpeedSurge
    {
        /// <summary>The contribution key on the ship's collection.</summary>
        public const string Key = "ectype.ship-speed-surge";

        private static bool _subscribed;

        /// <summary>
        /// The top-speed factor at progress u (0 to 1): s = 1 + magnitude * (1 - u). Magnitude 1 doubles
        /// top speed at u = 0; the factor falls linearly to 1 at u = 1.
        /// </summary>
        public static float TopSpeedFactor(float magnitude, float progress)
        {
            return 1f + magnitude * (1f - progress);
        }

        /// <summary>
        /// The drag factor that produces top-speed factor s: 1 / s^2.
        ///
        /// Drag is quadratic in speed (MovingSpacePlatform.ApplyFriction removes
        /// speed^2 * Friction * dt / Mass each tick), so full-thrust top speed is
        /// sqrt(thrust * EnginePower * ForwardPower / Friction) and drag times 1 / s^2 is top speed
        /// times s. Drag rather than forward thrust, because thrust acts only while the pilot holds
        /// forward and not at all with the helm empty, and the pilot aid's auto-brake is a thrust input
        /// scaled by the same ForwardPower, so a thrust boost stops a coasting ship sooner than vanilla.
        /// Vanilla already throws the ship forward on leaving the void (25000 / Mass on the state
        /// owner, 312.5 m/s on an 80-mass hull) and drag bleeds that to cruise speed in about two
        /// seconds; cutting drag lets that throw carry, with or without a pilot.
        ///
        /// The cut is isotropic: strafe and vertical top speed scale by s as well, and braking, the
        /// 20 m/s minimum-drag floor that stops a coasting ship included, is 1 / s^2 as strong.
        /// </summary>
        public static float DragFactor(float magnitude, float progress)
        {
            float s = TopSpeedFactor(magnitude, progress);
            return 1f / (s * s);
        }

        /// <summary>Start a surge from an arrival, at the arrival's own server stamp.</summary>
        public static TimedStatContribution Start(ShipArrival arrival, float magnitude, float durationSeconds,
                                                  Func<bool> keepAlive = null)
        {
            return Start(arrival.Ship, magnitude, durationSeconds, arrival.ServerMs, keepAlive);
        }

        /// <summary>
        /// Start (or replace) the surge on <paramref name="ship"/>. Magnitude is the extra top speed at
        /// the start as a fraction of normal (1 = double); above -1, and a negative one is a slow-down.
        /// Returns null, having applied nothing, for a null ship, a magnitude of 0 or at most -1, a
        /// non-positive duration, a ship outside normal flight, or any reason
        /// TimedStatContributions.Start refuses.
        /// </summary>
        public static TimedStatContribution Start(PlayerControlledShip ship, float magnitude,
                                                  float durationSeconds, int startServerMs,
                                                  Func<bool> keepAlive = null)
        {
            try
            {
                if (ship == null || ship.Stats == null || magnitude == 0f || !(magnitude > -1f) ||
                    float.IsInfinity(magnitude) || !(durationSeconds > 0f))
                {
                    Plugin.Log.LogWarning("Ectype: ship speed surge not started (ship " +
                                          (ship == null ? "null" : ship.name) + ", magnitude " + magnitude +
                                          ", " + durationSeconds + " s): it needs a ship, a magnitude " +
                                          "above -1 other than 0, and a positive duration.");
                    return null;
                }
                EnsureSubscribed();
                if (!ShipArrivals.InNormalFlight(ship))
                {
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: ship speed surge not started: the ship is not in " +
                                           "normal flight.");
                    return null;
                }

                float m = magnitude;
                return TimedStatContributions.StartUnchecked(Key, ship.Stats, FrameworkStats.ShipDrag,
                                                    ModifierType.ExponentialMultiplier,
                                                    u => DragFactor(m, u) - 1f,
                                                    durationSeconds, startServerMs, keepAlive,
                                                    "Ship Speed Surge");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: starting a ship speed surge failed: " + e);
                return null;
            }
        }

        /// <summary>End the surge on <paramref name="ship"/> now. No-op without one.</summary>
        public static void End(PlayerControlledShip ship, string reason = "cancelled")
        {
            if (ship == null || ship.Stats == null) return;
            TimedStatContribution live = TimedStatContributions.Find(ship.Stats, Key);
            if (live != null) TimedStatContributions.End(live, reason);
        }

        /// <summary>True while a surge runs on <paramref name="ship"/>.</summary>
        public static bool IsActive(PlayerControlledShip ship)
        {
            return ship != null && ship.Stats != null && TimedStatContributions.Find(ship.Stats, Key) != null;
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;
            ShipArrivals.LeftNormalFlight += OnLeftNormalFlight;
        }

        private static void OnLeftNormalFlight(PlayerControlledShip ship)
        {
            End(ship, "void jump started");
        }
    }
}
