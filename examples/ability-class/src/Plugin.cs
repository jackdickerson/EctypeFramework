using System;

using BepInEx;
using BepInEx.Logging;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleAbilityClass
{
    /// <summary>
    /// The class lives in the embedded manifest and the framework registers it. This plugin
    /// is loaded so BepInEx orders it after the framework and VoidManager, and so the
    /// framework's discovery scan finds the assembly.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Ability Class", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
        // Must equal the manifest's classId. Keep PLUGIN_VERSION equal to <Version> in the csproj.
#error Replace every REPLACE_ME (the plugin GUID here and the manifest ids) with your own unique values, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                BraceAbility.RegisterStatus();

                // The framework calls the factory once per character spawn and installs the
                // returned ability, pointing it at this class's hero perk.
                Ectype.ClassRegistry.RegisterAbilityFactory(
                    PLUGIN_GUID, () => new BraceAbility(cooldownSeconds: 20f, durationSeconds: 5f));
            }
            catch (Exception e)
            {
                Log.LogError("Ability registration failed: " + e);
            }

            Log.LogInfo("Example Ability Class v" + PLUGIN_VERSION + " loaded.");
        }
    }

    /// <summary>Every player in a room needs the same class mods, and VoidManager's version
    /// check enforces it for MultiplayerType.All.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.All; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "A minimal class with one coded ability."; } }

        public override SessionChangedReturn OnSessionChange(SessionChangedInput input)
        {
            return new SessionChangedReturn { SetMod_Session = true };
        }
    }
}
