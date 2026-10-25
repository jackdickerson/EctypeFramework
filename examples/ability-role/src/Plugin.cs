using System;

using BepInEx;
using BepInEx.Logging;

namespace ExampleAbilityRole
{
    /// <summary>
    /// The role lives in the embedded manifest and the framework registers it. This plugin
    /// is loaded so BepInEx orders it after the framework, and so the framework's discovery
    /// scan finds the assembly.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Ability Role", PLUGIN_VERSION)]
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
            try
            {
                BraceAbility.RegisterStatus();

                // The framework calls the factory once per character spawn and installs the
                // returned ability, pointing it at this role's hero perk.
                Ectype.RoleRegistry.RegisterAbilityFactory(
                    PLUGIN_GUID, () => new BraceAbility(cooldownSeconds: 20f, durationSeconds: 5f));
            }
            catch (Exception e)
            {
                Log.LogError("Ability registration failed: " + e);
            }

            Log.LogInfo("Example Ability Role v" + PLUGIN_VERSION + " loaded.");
        }
    }
}
