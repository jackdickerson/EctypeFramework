using System;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Ectype;

namespace ExampleSessionData
{
    /// <summary>
    /// Host-syncs one config entry, ArrivalSpread, through Ectype.SessionData.HostValue and logs
    /// the value in force on every client: the host's own config on the host, the host's value on
    /// every other client, live in the hub and frozen for a mission from its start.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Session Data", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private ConfigEntry<float> _arrivalSpread;
        private HostValue<float> _spread;

        private void Awake()
        {
            Log = Logger;
            try
            {
                _arrivalSpread = Config.Bind("General", "ArrivalSpread", 450f,
                    "Metres between ships when they leave the void. Only the host's setting counts.");

                // Same key on every client: here, the default "<config file>/General.ArrivalSpread".
                _spread = SessionData.HostValue(_arrivalSpread);
                _spread.Changed += OnSpreadChanged;
                ShipArrivals.Arrived += OnArrived;
                Log.LogInfo("Example Session Data v" + PLUGIN_VERSION + " loaded; key '" + _spread.Key + "'.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Session Data failed to start: " + e);
            }
        }

        private void OnDestroy()
        {
            if (_spread != null) _spread.Changed -= OnSpreadChanged;
            ShipArrivals.Arrived -= OnArrived;
        }

        private void OnSpreadChanged(float value)
        {
            try
            {
                Log.LogInfo("ArrivalSpread is now " + value + " m (" + _spread.Source +
                            (_spread.IsFromHost ? ", the host's" : ", this client's own") + ").");
            }
            catch (Exception e)
            {
                Log.LogError("Changed handler failed: " + e);
            }
        }

        private void OnArrived(ShipArrival arrival)
        {
            try
            {
                // Read at the moment of use: in a mission this is the value frozen at its start.
                Log.LogInfo("Arrived (" + arrival.Kind + "); spread in force: " + _spread.Value + " m from " +
                            _spread.Source + ".");
            }
            catch (Exception e)
            {
                Log.LogError("Arrival handler failed: " + e);
            }
        }
    }
}
