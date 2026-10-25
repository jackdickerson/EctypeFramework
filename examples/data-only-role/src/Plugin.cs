using System;

using BepInEx;
using BepInEx.Logging;

namespace ExampleRole
{
    /// <summary>
    /// The whole role lives in the embedded manifest; the framework registers it. This plugin
    /// is loaded so BepInEx orders it after the framework, and so the framework's discovery
    /// scan finds the assembly.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Role", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
        // Must equal the manifest's roleId. Keep PLUGIN_VERSION equal to <Version> in the csproj.
#error Replace every REPLACE_ME (the plugin GUID here and the manifest ids) with your own unique values, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Example Role v" + PLUGIN_VERSION + " loaded.");
        }
    }
}
