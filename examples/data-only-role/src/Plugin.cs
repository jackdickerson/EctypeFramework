using System;

using BepInEx;
using BepInEx.Logging;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleRole
{
    /// <summary>
    /// The whole role lives in the embedded manifest; the framework registers it. This plugin
    /// is loaded so BepInEx orders it after the framework and VoidManager, and so the
    /// framework's discovery scan finds the assembly.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Role", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
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

    /// <summary>Every player in a room needs the same role mods, and VoidManager's version
    /// check enforces it for MultiplayerType.All.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.All; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "A minimal data-only role for the Ectype Framework."; } }

        public override SessionChangedReturn OnSessionChange(SessionChangedInput input)
        {
            return new SessionChangedReturn { SetMod_Session = true };
        }
    }
}
