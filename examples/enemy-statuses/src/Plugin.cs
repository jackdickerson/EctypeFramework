using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Game;
using CG.Space;
using UnityEngine.InputSystem;

namespace ExampleEnemyStatuses
{
    /// <summary>
    /// Press F6 to slow the nearest NPC ship by half for five seconds, through
    /// Ectype.EnemyStatuses. A status is a template registered once, then applied to a target
    /// for a duration; the framework replicates the application and runs the timer.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Enemy Statuses", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
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
                // Register the template at startup, on every client, from a literal key:
                // the key crosses the network and a peer without it ignores the message.
                // MaxFlightSpeed is a stat every NPC ship registers; a stat the target does
                // not register is skipped silently.
                EnemyStatuses.Register(new EnemyStatusDef(SlowKey)
                {
                    DisplayName = "Slowed",
                    MaxStacks = 1,                    // re-applying refreshes the timer
                    Stance = StatusStance.Hostile,    // only a client on another side may apply it
                    OnApplied = (target, stacks) => Log.LogInfo("Slow applied to " + target.name),
                    OnRemoved = target => Log.LogInfo("Slow ended on " + (target != null ? target.name : "(destroyed)")),
                }.AddStat("MaxFlightSpeed", "percent", -0.5f));
                Log.LogInfo("Example Enemy Statuses v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Enemy Statuses failed to start: " + e);
            }
        }

        private const string SlowKey = PLUGIN_GUID + ".slow";

        private void Update()
        {
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb == null || !kb[Key.F6].wasPressedThisFrame) return;

                OrbitObject target = NearestEnemy();
                if (target == null) { Log.LogInfo("No NPC ship in the sector."); return; }

                // Any client on another side from the target may apply; the application
                // reaches every peer that registered the key, and each judges the stance
                // again. The attacker is optional and credits damage-over-time kills.
                StatusRefusal why = EnemyStatuses.TryApply(target, SlowKey, seconds: 5f, magnitude: 1f,
                                                           attacker: ClientGame.Current.PlayerShip);
                if (why != StatusRefusal.None) Log.LogInfo("Slow not applied: " + why);
            }
            catch (Exception e)
            {
                Log.LogError("Update failed: " + e);
            }
        }

        /// <summary>The enemy NPC ship closest to the player ship, or null.</summary>
        private static OrbitObject NearestEnemy()
        {
            PlayerControlledShip me = ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
            GameSessionSector sector = GameSessionManager.ActiveSector;
            if (me == null || sector == null) return null;
            TargetableShipsCategory ships = sector.GetSectorObjectCategory<TargetableShipsCategory>();
            if (ships == null) return null;

            SpaceCraft best = null;
            float bestSqr = float.MaxValue;
            foreach (SpaceCraft s in ships.SectorObjects)
            {
                if (s == null || s == me) continue;
                float d = (s.transform.position - me.transform.position).sqrMagnitude;
                if (d < bestSqr) { best = s; bestSqr = d; }
            }
            return best;
        }

    }
}
