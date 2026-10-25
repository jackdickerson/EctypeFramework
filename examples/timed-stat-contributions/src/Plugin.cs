using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using Gameplay.Utilities;
using CG.Space;

namespace ExampleTimedStat
{
    /// <summary>
    /// After every normal arrival from the void, adds a forward-thrust bonus to the player ship
    /// that falls linearly to nothing over eight seconds, through Ectype.TimedStatContributions.
    /// The window is anchored to the arrival's server timestamp, so every machine that runs this
    /// mod computes the same curve without any message.
    ///
    /// For contrast, a lasting contribution through Ectype.StatContributions: every player ship
    /// has 25% more hull for as long as it lives, applied as each ship spawns.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Timed Stat", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                ShipArrivals.Arrived += OnArrived;
                // Registered on every client, before any ship spawns. Amount answers the same on
                // every client (a constant here); null would mean no contribution on that ship.
                StatContributions.Register(new StatContribution
                {
                    Id = HullId,
                    Target = StatTarget.PlayerShip,
                    Stat = "MaxHitPoints",
                    Kind = SessionStatModifierKind.Percent,   // 0.25 is +25%
                    Amount = ship => 0.25f,
                    Source = "Reinforced hull",
                });
                Log.LogInfo("Example Timed Stat v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Timed Stat failed to start: " + e);
            }
        }

        private const string BoostKey = PLUGIN_GUID + ".thrust";
        private const string HullId = PLUGIN_GUID + "/hull";

        private void OnDestroy()
        {
            ShipArrivals.Arrived -= OnArrived;
            StatContributions.Unregister(HullId);
        }

        private static void OnArrived(ShipArrival arrival)
        {
            try
            {
                if (arrival.Kind != ShipArrivalKind.Normal || arrival.Ship == null) return;

                // A factor f is ExponentialMultiplier with an amount of f - 1: the amount
                // runs from 0.5 (x1.5) at progress 0 down to 0 (x1) at progress 1.
                // Starting the same key on the same collection again replaces it.
                TimedStatContribution c = TimedStatContributions.Start(
                    BoostKey, arrival.Ship.Stats, "ForwardPower",
                    ModifierType.ExponentialMultiplier,
                    TimedStatContributions.LinearDecay(0.5f),
                    durationSeconds: 8f,
                    startServerMs: arrival.ServerMs,      // same phase on every machine
                    label: "Arrival thrust");
                // Null means nothing was applied: a bad argument, an unknown stat, or a
                // window that already ended on this machine's clock.
                if (c == null) Log.LogWarning("Thrust contribution was not started.");
            }
            catch (Exception e)
            {
                Log.LogError("Arrival handler failed: " + e);
            }
        }
    }
}
