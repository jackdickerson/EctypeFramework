using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Space;
using Gameplay.Ship.VoidJump;
using HarmonyLib;
using Photon.Pun;

namespace Ectype
{
    /// <summary>How the player ship left the void.</summary>
    public enum ShipArrivalKind
    {
        /// <summary>A completed jump: the spin-down ended, or the approach did on a machine whose
        /// stream skipped the 3 s spin-down.</summary>
        Normal,

        /// <summary>Pulled out of an unstable tunnel. Vanilla zeroes the ship's velocity, then the
        /// state owner adds 300000 / Mass backward and a yaw spin.</summary>
        Interdiction,

        /// <summary>This machine went straight from a travelling state to normal flight, so it never
        /// saw which of the two exits ran (its stream stalled for longer than the 5 s interdiction).
        /// Treat it as not normal.</summary>
        Unknown,
    }

    /// <summary>One arrival from the void, as this machine saw it.</summary>
    public struct ShipArrival
    {
        /// <summary>The ship that left the void.</summary>
        public PlayerControlledShip Ship;
        /// <summary>How the ship left the void.</summary>
        public ShipArrivalKind Kind;

        /// <summary>When the ship left the void, on PhotonNetwork.ServerTimestamp: the replicated
        /// enter stamp of the spin-down or interdiction plus that state's fixed length, which is
        /// when the state owner's timer fired. A machine that skipped that state uses its own clock
        /// at the transition. Every machine starting a timed effect from this stamp agrees on its
        /// phase however late the transition reached it.</summary>
        public int ServerMs;
    }

    /// <summary>
    /// The void-jump state machine's arrivals and departures, raised on every machine.
    ///
    /// VoidJumpSystem.ChangeActiveStateByIndex is the only method that changes the jump state. The
    /// state owner (the helm sitter, or the master while the helm is empty) reaches it from
    /// UpdateState; every other machine reaches it from OnPhotonSerializeView with the replicated
    /// index, before ReadData. It returns early when the index is unchanged, so the run's first
    /// sector (the master's Start asks for Inactive while already Inactive) raises nothing, and a
    /// joiner's fresh system starts at Inactive and adopts the room's current index on its first
    /// read, which therefore can never be an arrival.
    ///
    /// The postfix runs after the old state's OnExit and the new state's OnEnter. Where the exit
    /// state ran, its OnExit entered the destination sector and started its objective; a machine
    /// whose stream skipped it enters through the room property on its own schedule.
    ///
    /// Every player ship has its own void-jump system, so a room with several ships raises
    /// Arrived and LeftNormalFlight once per ship per jump, each with that ship. FleetArrived is
    /// the once-per-jump event: this machine keeps the set of ships that have left normal flight,
    /// and raises it when the last of them is back in normal flight, having arrived, called the
    /// jump off, died or been destroyed.
    /// </summary>
    public static class ShipArrivals
    {
        /// <summary>Once per ship per arrival from the void, on every machine. With several player
        /// ships it fires once for each, each time with that ship in <see cref="ShipArrival.Ship"/>;
        /// compare that ship with the one the subscriber cares about rather than with the game's
        /// player-ship getter, which with several ships can name a different ship on each client.
        /// Each subscriber runs in its own try/catch; a throw is logged with the subscriber's name
        /// and the rest still run.</summary>
        public static event Action<ShipArrival> Arrived;

        /// <summary>
        /// Once per jump on every machine: after every ship that left normal flight is back in it
        /// and at least one of them arrived from the void. The value describes the jump as a
        /// whole: <see cref="ShipArrival.Ship"/> is the last ship to arrive,
        /// <see cref="ShipArrival.Kind"/> the worst kind any ship had (Interdiction, then Unknown,
        /// then Normal), and <see cref="ShipArrival.ServerMs"/> the latest stamp. With one ship it
        /// fires right after that ship's <see cref="Arrived"/>, with the same value. Same
        /// per-subscriber isolation as <see cref="Arrived"/>.
        /// </summary>
        public static event Action<ShipArrival> FleetArrived;

        /// <summary>The ship left normal flight (Inactive or ExitVectorSet, the set vanilla allows cruise
        /// control and thruster boosts in): an alignment toward a jump began, or a joiner adopted a
        /// room already in the void. Same per-subscriber isolation as <see cref="Arrived"/>.</summary>
        public static event Action<PlayerControlledShip> LeftNormalFlight;

        // Read once by name. A miss falls back to this machine's clock and is logged once.
        private static FieldInfo _spinDownEnter;
        private static FieldInfo _interdictionEnter;
        private static bool _fieldsResolved;
        private static bool _loggedFirstArrival;
        private static bool _warnedStamp;

        private static PlayerControlledShip _lastShip;
        private static VoidJumpSystem _lastSystem;

        // The jump in progress on this machine: ships out of normal flight, and what the ships
        // that already arrived reported.
        private static readonly List<PlayerControlledShip> _inTransit = new List<PlayerControlledShip>();
        private static int _jumpArrivals;
        private static ShipArrival _jump;
        private static bool _topologyBound;
        private static bool _loggedFirstFleetArrival;

        /// <summary>True while the ship is in normal flight: its jump state is Inactive or
        /// ExitVectorSet. A ship with no void-jump system is always in normal flight.</summary>
        public static bool InNormalFlight(PlayerControlledShip ship)
        {
            if (ship == null) return false;
            VoidJumpSystem system = SystemOf(ship);
            return system == null || IsNormalFlight(system.ActiveState);
        }

        private static VoidJumpSystem SystemOf(PlayerControlledShip ship)
        {
            if (!ReferenceEquals(ship, _lastShip) || _lastSystem == null)
            {
                _lastShip = ship;
                _lastSystem = ship.GetComponent<VoidJumpSystem>();
            }
            return _lastSystem;
        }

        private static bool IsNormalFlight(VoidJumpState state)
        {
            return state is VoidJumpInactive || state is VoidJumpExitVectorSet;
        }

        internal static void OnStateChanged(VoidJumpSystem system, VoidJumpState old, VoidJumpState now)
        {
            if (system == null || old == null || now == null || ReferenceEquals(old, now)) return;
            PlayerControlledShip ship = system.PlayerShip;
            if (ship != null)
            {
                _lastShip = ship;
                _lastSystem = system;
            }

            bool arrived = false;
            if (now is VoidJumpInactive)
            {
                ShipArrivalKind kind;
                int stamp;
                if (Classify(old, out kind, out stamp))
                {
                    RaiseArrived(ship, kind, stamp, old);
                    if (ship != null)
                    {
                        arrived = true;
                        BackInNormalFlight(ship, new ShipArrival { Ship = ship, Kind = kind, ServerMs = stamp });
                    }
                }
            }

            if (IsNormalFlight(old) && !IsNormalFlight(now))
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: ship left normal flight (" + old.GetType().Name +
                                       " -> " + now.GetType().Name + ").");
                if (ship != null && !_inTransit.Contains(ship)) _inTransit.Add(ship);
                BindTopology();
                Raise(LeftNormalFlight, ship, "LeftNormalFlight");
            }
            else if (!arrived && ship != null && !IsNormalFlight(old) && IsNormalFlight(now))
            {
                // A jump called off before the void: the ship leaves the transit set without an
                // arrival.
                BackInNormalFlight(ship, null);
            }
        }

        /// <summary>A ship is back in normal flight, with its arrival or without one. When no ship
        /// is left in transit and at least one arrived, the jump is over.</summary>
        private static void BackInNormalFlight(PlayerControlledShip ship, ShipArrival? arrival)
        {
            _inTransit.Remove(ship);
            if (arrival.HasValue)
            {
                ShipArrival a = arrival.Value;
                if (_jumpArrivals == 0)
                {
                    _jump = a;
                }
                else
                {
                    _jump.Ship = a.Ship;
                    if (Severity(a.Kind) > Severity(_jump.Kind)) _jump.Kind = a.Kind;
                    if (unchecked(a.ServerMs - _jump.ServerMs) > 0) _jump.ServerMs = a.ServerMs;
                }
                _jumpArrivals++;
            }
            TryCompleteJump();
        }

        private static int Severity(ShipArrivalKind kind)
        {
            switch (kind)
            {
                case ShipArrivalKind.Interdiction: return 2;
                case ShipArrivalKind.Unknown: return 1;
                default: return 0;
            }
        }

        /// <summary>Drops ships that died or were destroyed mid-jump, then raises FleetArrived
        /// once if the jump is over.</summary>
        private static void TryCompleteJump()
        {
            for (int i = _inTransit.Count - 1; i >= 0; i--)
                if (_inTransit[i] == null || _inTransit[i].StartedDeath) _inTransit.RemoveAt(i);
            if (_inTransit.Count > 0 || _jumpArrivals == 0) return;

            ShipArrival jump = _jump;
            int ships = _jumpArrivals;
            _jumpArrivals = 0;
            _jump = default(ShipArrival);

            string line = "Ectype: fleet arrived from the void (" + ships + " ship(s), " + jump.Kind +
                          ", server " + jump.ServerMs + ", " +
                          (FleetArrived == null ? 0 : FleetArrived.GetInvocationList().Length) + " subscribers).";
            if (!_loggedFirstFleetArrival)
            {
                _loggedFirstFleetArrival = true;
                Plugin.Log.LogInfo(line + " Further fleet arrivals log under [Debug] VerboseLogging.");
            }
            else if (Plugin.Verbose)
            {
                Plugin.Log.LogInfo(line);
            }
            Raise(FleetArrived, jump, "FleetArrived");
        }

        /// <summary>A ship dying or destroyed mid-jump never arrives; the topology change that
        /// reports it re-checks the jump.</summary>
        private static void BindTopology()
        {
            if (_topologyBound) return;
            _topologyBound = true;
            Topology.Changed += () =>
            {
                if (_inTransit.Count == 0) return;
                if (Topology.Ships.Count == 0)
                {
                    // Every ship gone (mission over, scene unloaded): no jump to complete.
                    _inTransit.Clear();
                    _jumpArrivals = 0;
                    return;
                }
                TryCompleteJump();
            };
        }

        /// <summary>
        /// Only a travelling-side state leads to Inactive as an arrival; ExitVectorSet, Aligning and
        /// SpinningUp lead there when a jump is called off.
        /// </summary>
        private static bool Classify(VoidJumpState old, out ShipArrivalKind kind, out int stamp)
        {
            int now = PhotonNetwork.ServerTimestamp;
            stamp = now;
            kind = ShipArrivalKind.Normal;

            if (old is VoidJumpSpinningDown)
            {
                stamp = EndStamp(old, SpinDownEnter(), VoidJumpSpinningDown.SPIN_DOWN_TIME_MS, now);
                return true;
            }
            if (old is VoidJumpApproachingDestination) return true;
            if (old is VoidJumpInterdiction)
            {
                kind = ShipArrivalKind.Interdiction;
                stamp = EndStamp(old, InterdictionEnter(), VoidJumpInterdiction.INTERDICTION_DURATION_MS, now);
                return true;
            }
            if (old is VoidJumpTravellingStable || old is VoidJumpTravellingUnstable)
            {
                kind = ShipArrivalKind.Unknown;
                return true;
            }
            return false;
        }

        /// <summary>The state's replicated enter stamp plus its length. A stamp later than this
        /// machine's clock is not an end that has happened; the clock wins.</summary>
        private static int EndStamp(VoidJumpState state, FieldInfo enter, int lengthMs, int now)
        {
            if (enter == null) return now;
            try
            {
                int end = unchecked((int)enter.GetValue(state) + lengthMs);
                return unchecked(now - end) >= 0 ? end : now;
            }
            catch (Exception e)
            {
                if (!_warnedStamp)
                {
                    _warnedStamp = true;
                    Plugin.Log.LogWarning("Ectype: reading " + state.GetType().Name + ".enterTimestamp " +
                                          "failed; arrivals are stamped with this machine's clock: " +
                                          e.Message);
                }
                return now;
            }
        }

        private static FieldInfo SpinDownEnter() { ResolveFields(); return _spinDownEnter; }
        private static FieldInfo InterdictionEnter() { ResolveFields(); return _interdictionEnter; }

        private static void ResolveFields()
        {
            if (_fieldsResolved) return;
            _fieldsResolved = true;
            _spinDownEnter = IntField(typeof(VoidJumpSpinningDown));
            _interdictionEnter = IntField(typeof(VoidJumpInterdiction));
        }

        private static FieldInfo IntField(Type t)
        {
            FieldInfo f = Refl.Field(t, "enterTimestamp");
            if (f != null && f.FieldType == typeof(int)) return f;
            Plugin.Log.LogWarning("Ectype: " + t.Name + ".enterTimestamp (int) not found; arrivals " +
                                  "from it are stamped with this machine's clock.");
            return null;
        }

        private static void RaiseArrived(PlayerControlledShip ship, ShipArrivalKind kind, int stamp,
                                         VoidJumpState old)
        {
            if (ship == null)
            {
                Plugin.Log.LogWarning("Ectype: an arrival from the void (" + kind + ") came from a " +
                                      "void-jump system with no PlayerShip; not raised.");
                return;
            }

            int lateMs = unchecked(PhotonNetwork.ServerTimestamp - stamp);
            string line = "Ectype: ship arrived from the void (" + kind + ", from " + old.GetType().Name +
                          ", server " + stamp + ", seen " + lateMs + " ms later here, " +
                          (Arrived == null ? 0 : Arrived.GetInvocationList().Length) + " subscribers).";
            if (!_loggedFirstArrival)
            {
                _loggedFirstArrival = true;
                Plugin.Log.LogInfo(line + " Further arrivals log under [Debug] VerboseLogging.");
            }
            else if (Plugin.Verbose)
            {
                Plugin.Log.LogInfo(line);
            }

            Raise(Arrived, new ShipArrival { Ship = ship, Kind = kind, ServerMs = stamp }, "Arrived");
        }

        private static void Raise<T>(Action<T> handlers, T arg, string what)
        {
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)d)(arg);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: ShipArrivals." + what + " subscriber " +
                                        (d.Method.DeclaringType != null ? d.Method.DeclaringType.FullName : "?") +
                                        "." + d.Method.Name + " threw: " + e);
                }
            }
        }
    }

    /// <summary>
    /// Prefix records the state being left; the postfix reads the one entered. A postfix runs after
    /// the original's early return too, which the old == new check in OnStateChanged covers.
    /// </summary>
    [HarmonyPatch(typeof(VoidJumpSystem), "ChangeActiveStateByIndex")]
    internal static class VoidJumpSystem_ChangeActiveStateByIndex_Patch
    {
        private static void Prefix(VoidJumpSystem __instance, out VoidJumpState __state)
        {
            __state = null;
            try { __state = __instance.ActiveState; }
            catch (Exception e) { Plugin.Log.LogError("Ectype: reading the void-jump state failed: " + e); }
        }

        private static void Postfix(VoidJumpSystem __instance, VoidJumpState __state)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try
            {
                ShipArrivals.OnStateChanged(__instance, __state, __instance.ActiveState);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: ship arrival dispatch failed: " + e);
            }
        }
    }
}
