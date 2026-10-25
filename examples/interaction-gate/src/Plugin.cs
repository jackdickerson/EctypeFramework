using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;

namespace ExampleInteractionGate
{
    /// <summary>
    /// Only the room's host may take the helm, through Ectype.InteractionGate. Every other
    /// player sees no prompt on the helm's seat and a warning when they try it; a request from a
    /// player whose game skipped that check is dropped by the seat's owner.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Interaction Gate", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        // The gate's id: unique across every mod, so prefix it with the plugin GUID.
        private const string GateId = PLUGIN_GUID + ".host-helm";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                if (InteractionGate.Register(GateId, HostHelm))
                    Log.LogInfo("Example Interaction Gate v" + PLUGIN_VERSION + " loaded: the helm is host-only.");
            }
            catch (Exception e)
            {
                Log.LogError("Could not register the interaction gate: " + e);
            }
        }

        private void OnDestroy()
        {
            InteractionGate.Unregister(GateId);
        }

        /// <summary>
        /// Called on the acting player's game for the prompt and the warning, and on the seat
        /// owner's game when the request arrives. It reads only the actor's identity, which both
        /// games agree on, so the two answers match.
        /// </summary>
        private static Refusal HostHelm(GateQuery q)
        {
            if (q.Kind != InteractionKind.Helm || q.ActorIsHost) return Refusal.Allowed;
            return Refusal.Because("helm is host-only", "ONLY THE HOST MAY PILOT THIS SHIP.");
        }
    }
}
