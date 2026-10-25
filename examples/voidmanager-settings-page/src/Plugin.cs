using System;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ExampleSettingsPage
{
    /// <summary>
    /// The utility example with an optional F5 page: it logs each arrival from the void, and
    /// with VoidManager installed the page in VoidManager's menu turns the logging on and off.
    /// Without VoidManager the mod runs in full and the page is absent.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Settings Page", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    // Soft: BepInEx loads VoidManager before this plugin when it is installed, and loads this
    // plugin either way.
    [BepInDependency(VoidManagerPage.VoidManagerGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> LogArrivals;
        internal static int ArrivalCount;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            LogArrivals = Config.Bind("General", "LogArrivals", true,
                                      "Log a line each time the ship leaves the void.");
            try
            {
                Ectype.ShipArrivals.Arrived += OnArrived;
            }
            catch (Exception e)
            {
                Log.LogError("Could not subscribe to ShipArrivals.Arrived: " + e);
            }

            _harmony = new Harmony(PLUGIN_GUID);
            try
            {
                Log.LogInfo(VoidManagerPage.TryInstall(_harmony)
                    ? "VoidManager loaded: the F5 page is added when VoidManager builds its menu."
                    : "VoidManager not loaded: no F5 page. Everything else runs.");
            }
            catch (Exception e)
            {
                Log.LogError("F5 page hook failed; the mod runs without it: " + e);
            }

            Log.LogInfo("Example Settings Page v" + PLUGIN_VERSION + " loaded.");
        }

        private void OnDestroy()
        {
            Ectype.ShipArrivals.Arrived -= OnArrived;
        }

        private static void OnArrived(Ectype.ShipArrival arrival)
        {
            try
            {
                ArrivalCount++;
                if (LogArrivals.Value)
                    Log.LogInfo("Ship arrived from the void: " + arrival.Kind +
                                " (server time " + arrival.ServerMs + " ms)");
            }
            catch (Exception e)
            {
                Log.LogError("Arrival handler failed: " + e);
            }
        }
    }

    /// <summary>
    /// What the F5 page shows. Nothing here names a VoidManager type: the page class that
    /// VoidManagerPage builds at runtime calls these two methods, which are public because
    /// that class lives in another assembly.
    /// </summary>
    public static class SettingsPage
    {
        public static string Name()
        {
            return "Example Settings Page";
        }

        public static void Draw()
        {
            // The setter saves the config file only when the value changes.
            Plugin.LogArrivals.Value = GUILayout.Toggle(Plugin.LogArrivals.Value,
                                                        " Log each arrival from the void");
            GUILayout.Label("Arrivals since launch: " + Plugin.ArrivalCount);
            GUILayout.Label("Config: BepInEx/config/" + Plugin.PLUGIN_GUID + ".cfg");
        }
    }
}
