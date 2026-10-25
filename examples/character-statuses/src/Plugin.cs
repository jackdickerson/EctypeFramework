using System;

using BepInEx;
using BepInEx.Logging;
using CG.Game;
using Ectype;
using CG.Game.Player;
using UnityEngine;
using UnityEngine.InputSystem;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleCharacterStatuses
{
    /// <summary>
    /// F6: take half damage for ten seconds, applied to this player's own character.
    /// F7: slow the nearest crewmate within 5 m for three seconds, sent as a request to that
    /// crewmate's client, which checks it and applies it there.
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

        private const string GuardKey = PLUGIN_GUID + ".guard";
        private const string SlowKey = PLUGIN_GUID + ".slow";

        // The crewmate must be this close on this client before a request is sent.
        private const float SlowReach = 5f;

        private void Awake()
        {
            Log = Logger;
            try
            {
                // Register once at startup, from a literal key, on every client.
                CharacterStatuses.Register(new CharacterStatusDef(GuardKey)
                {
                    DisplayName = "Guarded",
                    MaxStacks = 1,
                    OnApplied = (player, stacks) => Log.LogInfo("Guard on."),
                    OnRemoved = player => Log.LogInfo("Guard off."),
                    // Vulnerability is a damage multiplier; flat -0.5 takes half damage.
                    // AddAllVulnerabilities covers each damage type and the aggregate.
                }.AddAllVulnerabilities("flat", -0.5f));

                CharacterStatuses.Register(new CharacterStatusDef(SlowKey)
                {
                    DisplayName = "Slowed",
                    // Accept requests from other clients, judged on the slowed player's client.
                    Replicated = true,
                    Stance = StatusStance.Friendly,      // crewmates only
                    MaxRange = SlowReach + 1f,           // a metre of slack for network delay
                    RequiresLineOfSight = true,
                    MinInterval = 2f,                    // one slow per 2 s from each requester
                    Burst = 1,
                    MaxSeconds = 3f,
                    MaxMagnitude = 1f,
                    // Other players get a presentation copy: no stats, no callbacks.
                    Presented = true,
                    OnApplied = (player, stacks) => Log.LogInfo("Slowed by a crewmate."),
                    OnRemoved = player => Log.LogInfo("No longer slowed."),
                }.AddStat("MotorAcceleration", "percent", -0.4f));

                Log.LogInfo("Example Character Statuses v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Character Statuses failed to start: " + e);
            }
        }

        private void Update()
        {
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb == null) return;
                Player me = LocalPlayer.I;
                if (me == null) return;

                // Applying again refreshes the timer. The status is removed on expiry,
                // death and despawn; Remove(me, key) or RemoveAll(me) ends it early.
                if (kb[Key.F6].wasPressedThisFrame)
                    CharacterStatuses.Apply(me, GuardKey, seconds: 10f);

                if (kb[Key.F7].wasPressedThisFrame)
                {
                    Player mate = NearestCrewmate(me, SlowReach);
                    if (mate == null)
                    {
                        Log.LogInfo("No crewmate within " + SlowReach + " m.");
                        return;
                    }
                    // None means sent: the crewmate's client checks again and can refuse.
                    CharacterStatusRefusal r = CharacterStatuses.Request(mate, SlowKey, seconds: 3f);
                    Log.LogInfo(r == CharacterStatusRefusal.None
                                    ? "Asked " + mate.name + "'s client to slow them."
                                    : "Slow on " + mate.name + " refused here: " + r + ".");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Update failed: " + e);
            }
        }

        /// <summary>The nearest other player's character within reach, or null. Every
        /// player's character is in ClientGame.Current.Players.</summary>
        private static Player NearestCrewmate(Player me, float reach)
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return null;
            Player best = null;
            float bestDistance = reach;
            foreach (Player p in game.Players)
            {
                if (p == null || p == me) continue;
                float d = Vector3.Distance(p.transform.position, me.transform.position);
                if (d <= bestDistance)
                {
                    best = p;
                    bestDistance = d;
                }
            }
            return best;
        }
    }

    /// <summary>All: the slowed player's client judges and applies the request, and every
    /// client that should see the slow draws it, so each needs the template. Applying
    /// statuses changes what a run contains, so the plugin flags the session.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.All; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Halves your damage on F6; slows a nearby crewmate on F7."; } }
        public override SessionChangedReturn OnSessionChange(SessionChangedInput input)
        {
            return new SessionChangedReturn { SetMod_Session = true };
        }
    }
}
