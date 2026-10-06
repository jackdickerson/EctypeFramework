using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Game;
using CG.Space;
using UnityEngine.InputSystem;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleEnemyAbilities
{
    /// <summary>
    /// F7 logs every special ability on the nearest NPC ship and its turrets, through
    /// Ectype.EnemyAbilities. F8 applies a status flagged SuspendsAbilities for five seconds,
    /// which is the only way to suspend abilities, so the two APIs are used together.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Enemy Abilities", PLUGIN_VERSION)]
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
                EnemyStatuses.Register(new EnemyStatusDef(HoldKey)
                {
                    DisplayName = "Held",
                    SuspendsAbilities = true,   // casts hold, cooldowns stop, nothing new starts
                });
                Log.LogInfo("Example Enemy Abilities v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Enemy Abilities failed to start: " + e);
            }
        }

        private const string HoldKey = PLUGIN_GUID + ".hold";

        private void Update()
        {
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb == null) return;

                if (kb[Key.F7].wasPressedThisFrame)
                {
                    OrbitObject target = NearestEnemy();
                    if (target == null) { Log.LogInfo("No NPC ship in the sector."); return; }

                    // The target and its turrets, one entry per ability whatever game system
                    // runs it. Basic weapon fire and movement are not abilities.
                    var list = EnemyAbilities.Describe(target);
                    Log.LogInfo(target.name + ": " + list.Count + " ability(ies), suspended: " +
                                EnemyAbilities.IsSuspended(target));
                    foreach (EnemyAbilityInfo a in list)
                        Log.LogInfo("  " + a.Name + " [" + a.Kind + "] " + a.Phase);
                }

                if (kb[Key.F8].wasPressedThisFrame)
                {
                    OrbitObject target = NearestEnemy();
                    if (target != null)
                        EnemyStatuses.Apply(target, HoldKey, seconds: 5f, magnitude: 1f,
                                            attacker: null);
                }
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

    /// <summary>All: suspension is a status, and a status takes effect only on clients that
    /// registered it. Applying one flags the session.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.All; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Lists an enemy's abilities on F7 and suspends them for five seconds on F8."; } }
        public override SessionChangedReturn OnSessionChange(SessionChangedInput input)
        {
            return new SessionChangedReturn { SetMod_Session = true };
        }
    }
}
