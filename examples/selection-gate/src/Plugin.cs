using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleSelectionGate
{
    /// <summary>
    /// Tracks whether every member of the room runs the same build of this mod, using
    /// Ectype.SelectionGate, and logs each change of the result. A mod that ships content a
    /// member without it cannot load would refuse or revert that content where this sample logs.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Selection Gate", PLUGIN_VERSION)]
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
                // Register once, from a literal GUID that equals this plugin's GUID: the
                // gate looks the mod up in VoidManager's mod list by it. The callback runs
                // on the main thread whenever the result for this GUID changes, including
                // once now.
                SelectionGate.Register(PLUGIN_GUID, OnGateChanged);
                Log.LogInfo("Example Selection Gate v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Selection Gate failed to start: " + e);
            }
        }

        private static void OnGateChanged(GateResult result)
        {
            try
            {
                // Verdict is only meaningful on the host. A client gets NotHost.
                Log.LogInfo("Selection gate: " + result.Verdict + " (host build " +
                            result.HostVersion + ").");
                if (result.Verdict == GateVerdict.Fail)
                    Log.LogWarning("Members who cannot run this mod's content: " +
                                   result.DescribeFailures());   // "Bob (missing), ..."
            }
            catch (Exception e)
            {
                Log.LogError("Gate callback failed: " + e);
            }
        }
    }

    /// <summary>Session: a room may contain members without this mod, which is the case the gate
    /// exists for. Presence of a Session mod flags the session as modded.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.Session; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Logs whether every room member runs the same build of this mod."; } }
    }
}
