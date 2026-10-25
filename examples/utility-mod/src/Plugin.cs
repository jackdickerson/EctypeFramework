using System;

using BepInEx;
using BepInEx.Logging;

namespace ExampleUtility
{
    /// <summary>
    /// A mod that is not a role and makes one framework call: it subscribes to
    /// Ectype.ShipArrivals.Arrived, which fires on this machine each time the player ship
    /// leaves the void, and logs how it left.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Utility", PLUGIN_VERSION)]
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
                Ectype.ShipArrivals.Arrived += OnArrived;
                Log.LogInfo("Example Utility v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Could not subscribe to ShipArrivals.Arrived: " + e);
            }
        }

        private void OnDestroy()
        {
            Ectype.ShipArrivals.Arrived -= OnArrived;
        }

        private static void OnArrived(Ectype.ShipArrival arrival)
        {
            try
            {
                Log.LogInfo("Ship arrived from the void: " + arrival.Kind +
                            " (server time " + arrival.ServerMs + " ms)");
            }
            catch (Exception e)
            {
                Log.LogError("Arrival handler failed: " + e);
            }
        }
    }
}
