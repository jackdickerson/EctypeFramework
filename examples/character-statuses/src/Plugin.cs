using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Game.Player;
using UnityEngine.InputSystem;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleCharacterStatuses
{
    /// <summary>
    /// Press F6 to take half damage for ten seconds, through Ectype.CharacterStatuses. A
    /// character status is local to the client that applies it, which is where character
    /// damage is resolved.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Character Statuses", PLUGIN_VERSION)]
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
                // Register once at startup, from a literal key.
                CharacterStatuses.Register(new CharacterStatusDef(GuardKey)
                {
                    DisplayName = "Guarded",
                    MaxStacks = 1,
                    OnApplied = (player, stacks) => Log.LogInfo("Guard on."),
                    OnRemoved = player => Log.LogInfo("Guard off."),
                    // Vulnerability is a damage multiplier; flat -0.5 takes half damage.
                    // AddAllVulnerabilities covers each damage type and the aggregate.
                }.AddAllVulnerabilities("flat", -0.5f));
                Log.LogInfo("Example Character Statuses v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Character Statuses failed to start: " + e);
            }
        }

        private const string GuardKey = PLUGIN_GUID + ".guard";

        private void Update()
        {
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb == null || !kb[Key.F6].wasPressedThisFrame) return;

                Player me = LocalPlayer.I;
                if (me == null) return;

                // Applying again refreshes the timer. The status is removed on expiry,
                // death and despawn; Remove(me, key) or RemoveAll(me) ends it early.
                CharacterStatuses.Apply(me, GuardKey, seconds: 10f);
            }
            catch (Exception e)
            {
                Log.LogError("Update failed: " + e);
            }
        }
    }

    /// <summary>Session: the status only changes this player's own damage intake, so other
    /// members need not run the mod, but presence flags the session.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.Session; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Halves damage taken for ten seconds on F6."; } }
    }
}
