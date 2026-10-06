using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Space;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleShipArrivals
{
    /// <summary>
    /// Logs each time the player ship leaves the void and each time it leaves normal flight,
    /// through Ectype.ShipArrivals. The events fire on every machine in the room.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Ship Arrivals", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
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
                ShipArrivals.LeftNormalFlight += OnLeftNormalFlight;
                Log.LogInfo("Example Ship Arrivals v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Ship Arrivals failed to start: " + e);
            }
        }

        private void OnDestroy()
        {
            ShipArrivals.Arrived -= OnArrived;
            ShipArrivals.LeftNormalFlight -= OnLeftNormalFlight;
        }

        private static void OnArrived(ShipArrival arrival)
        {
            try
            {
                // Unknown means this machine never saw which exit ran: treat it as not Normal.
                Log.LogInfo("Arrived: " + arrival.Kind + " at server time " + arrival.ServerMs +
                            " ms; in normal flight: " + ShipArrivals.InNormalFlight(arrival.Ship));
            }
            catch (Exception e)
            {
                Log.LogError("Arrival handler failed: " + e);
            }
        }

        private static void OnLeftNormalFlight(PlayerControlledShip ship)
        {
            try
            {
                Log.LogInfo("Ship left normal flight (a jump alignment began).");
            }
            catch (Exception e)
            {
                Log.LogError("Left-flight handler failed: " + e);
            }
        }
    }

    /// <summary>Client: the sample only reads, so a room does not require it and it does not
    /// flag the session.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.Client; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Logs ship arrivals and departures from normal flight."; } }
    }
}
