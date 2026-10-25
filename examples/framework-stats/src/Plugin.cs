using System;

using BepInEx;
using BepInEx.Logging;
using System.Collections.Generic;
using Ectype;
using CG.Game.Player;
using Gameplay.Utilities;
using UnityEngine.InputSystem;

namespace ExampleFrameworkStat
{
    /// <summary>
    /// F6 toggles a +50% walking top speed on the local character by contributing a modifier
    /// against a stat the framework registers, Ectype.FrameworkStats.MotorAcceleration. The
    /// game's own modifier stack composes it with any perk or other mod touching the same stat.
    /// It also registers the Hostile Surge mutator, which puts the session stat
    /// HostileSpawnScale at x1.5 for a run that has it active.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Framework Stat", PLUGIN_VERSION)]
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
                // Registration normally runs on its own once the game loads; calling it here
                // makes Id() answer from Awake. The id is 0 until registered.
                FrameworkStats.EnsureRegistered();
                RegisterHostileSurge();
                Log.LogInfo("Example Framework Stat v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Framework Stat failed to start: " + e);
            }
        }

        /// <summary>
        /// A mutator in the game's own Mutators menu. While it is active, the host's session
        /// collection holds HostileSpawnScale at x1.5 and every client reads that value. The
        /// framework registers the asset, makes it selectable, and contributes the modifier
        /// before the host publishes the session's stats; this mod writes no session code.
        /// </summary>
        private static void RegisterHostileSurge()
        {
            MutatorSpec spec = new MutatorSpec
            {
                Owner = PLUGIN_GUID,
                Key = "hostile-surge",
                DisplayName = "Hostile Surge",
                Description = "Hostile spawn groups are half again as large.",
            };
            spec.Effects.Add(new SessionStatEffect
            {
                Stat = "ectype/HostileSpawnScale",
                Kind = SessionStatModifierKind.Factor,
                Amount = 1.5f,
            });
            // Pending until the game's containers exist; the framework completes it.
            MutatorHandle surge = SessionStatMutators.Register(spec);
            if (surge.State == ContentState.Failed)
                Log.LogError("Hostile Surge refused: " + surge.Reason);
        }

        /// <summary>Identity of this mod's modifier, so it can be removed by source.</summary>
        private sealed class WalkBoost : IModifierSource { }

        private WalkBoost _active;

        private void Update()
        {
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb == null || !kb[Key.F6].wasPressedThisFrame) return;

                Player me = LocalPlayer.I;
                if (me == null || me.Stats == null) return;

                if (_active != null)
                {
                    me.Stats.RemoveModifier(_active);
                    _active = null;
                    Log.LogInfo("Walk boost off.");
                    return;
                }

                int id = FrameworkStats.Id(FrameworkStats.MotorAcceleration);
                StatBase stat;
                if (id == 0 || !me.Stats.Stats.TryGetValue(id, out stat))
                {
                    // The stat is wired onto the character at spawn. Before that a modifier
                    // would apply to nothing and fail silently.
                    Log.LogWarning("MotorAcceleration is not registered on this character yet.");
                    return;
                }

                // A factor f is ExponentialMultiplier with an amount of f - 1: x1.5.
                StatMod mod = new StatMod(
                    new FloatModifier(0.5f, ModifierType.ExponentialMultiplier), id);
                _active = new WalkBoost();
                me.Stats.ApplyModifiers(new List<StatMod> { mod }, _active, true);
                Log.LogInfo("Walk boost on.");
            }
            catch (Exception e)
            {
                Log.LogError("Update failed: " + e);
            }
        }
    }
}
