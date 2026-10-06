using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleSpeedSurge
{
    /// <summary>
    /// After every normal arrival from the void, raises the player ship's top speed by half and
    /// lets it decay to normal over fifteen seconds, through Ectype.ShipSpeedSurge. The surge is
    /// built on the ship's drag, so it works whether or not the pilot holds thrust.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Speed Surge", PLUGIN_VERSION)]
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
                Log.LogInfo("Example Speed Surge v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Speed Surge failed to start: " + e);
            }
        }

        private void OnDestroy()
        {
            ShipArrivals.Arrived -= OnArrived;
        }

        private static void OnArrived(ShipArrival arrival)
        {
            try
            {
                // Normal arrivals only: the cut in drag also carries an interdiction's
                // backward fling, which Unknown and Interdiction arrivals would inherit.
                if (arrival.Kind != ShipArrivalKind.Normal) return;

                // Magnitude 1 doubles top speed at the start; 0.5 is x1.5. Every machine
                // starts it from the arrival: drag is read by each machine's own simulation
                // and stat changes are not replicated.
                var c = ShipSpeedSurge.Start(arrival, magnitude: 0.5f, durationSeconds: 15f);
                Log.LogInfo(c != null ? "Surge started." : "Surge refused (not in normal flight).");
            }
            catch (Exception e)
            {
                Log.LogError("Arrival handler failed: " + e);
            }
        }
    }

    /// <summary>Session: every machine must start the surge. A machine that joined after the
    /// arrival has none, and if it then takes the helm the surge ends early for everyone.
    /// Presence flags the session.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.Session; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Surges the ship's top speed after each normal arrival."; } }
    }
}
