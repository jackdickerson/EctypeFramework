using System;
using System.Runtime.CompilerServices;

using CG.Client.Player.Interactions.Build;
using CG.Game.Player;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// SystemModuleConstructionSpeed: a factor (authored 1) on how fast a player installs, uninstalls and
    /// upgrades ship modules, on the player's own collection, so character-target perks reach it.
    ///
    /// There is no field to sink into. The game times all three jobs on the master: its
    /// BuildProcessController runs every BuildProcess there, and each Run adds Time.deltaTime
    /// to the process's StateTimer, which SetState zeroes on every state change. Working ends
    /// once the timer passes the shared GameConfiguration's BuildBoxConstructDuration (install)
    /// or DeconstructDuration (uninstall and upgrade); Completing - the fold, in which the
    /// module already exists - ends at the literal 3.25 s. Those durations are one value for
    /// the whole room, so the factor is applied to the clock of the one process instead: while
    /// a process is Working or Completing, each Run first adds deltaTime * (factor - 1), and
    /// the game's own comparisons do the rest. The factor read is that of RequesterActorId's
    /// player - the one who put the build box into the socket, pressed uninstall or used the
    /// upgrader - not of whoever holds the perk.
    ///
    /// The master carries every player's character-target perk modifiers (the perk loader
    /// re-applies each player's published levels on every client), and every replica gets the
    /// stat registered by the Player.Awake postfix, so the master reads the installer's value
    /// whoever the installer is. The 1 s placement check (Waiting) is untouched. Two visuals
    /// run on each client at stock speed and are not scaled: the build box's progress diodes
    /// (started by the FoldOut/FoldIn RPC with the stock duration, so at a factor above 1 they
    /// are short of full when the CompleteProcess RPC arrives), and the box timeline's fold
    /// after its Complete marker, which an install cuts short when the master destroys the
    /// box at 3.25 s / factor.
    /// </summary>
    internal static class BuildStats
    {
        // Never stop the clock outright.
        private const float MinFactor = 0.1f;

        private static Func<BuildProcess, float> _getTimer;
        private static Action<BuildProcess, float> _setTimer;
        private static bool _accessorsFailed;

        /// <summary>The player each process was started by, resolved once.</summary>
        private sealed class Installer
        {
            public Player Player;
        }

        private static readonly ConditionalWeakTable<BuildProcess, Installer> _installers =
            new ConditionalWeakTable<BuildProcess, Installer>();

        public static void Wire(Player player)
        {
            try
            {
                if (player == null || player.Stats == null || player.Stats.Stats == null) return;
                FrameworkStats.EnsureRegistered();
                int id = FrameworkStats.Id(FrameworkStats.SystemModuleConstructionSpeed);
                if (id == 0 || player.Stats.Stats.ContainsKey(id)) return;

                ModifiableFloat stat = (ModifiableFloat)1f;
                player.Stats.RegisterStat(id, ref stat);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: registering " + FrameworkStats.SystemModuleConstructionSpeed +
                                    " failed - installs run at the stock rate: " + e);
            }
        }

        private static float Factor(Player player)
        {
            if (player == null || player.Stats == null || player.Stats.Stats == null) return 1f;
            int id = FrameworkStats.Id(FrameworkStats.SystemModuleConstructionSpeed);
            StatBase sb;
            if (id == 0 || !player.Stats.Stats.TryGetValue(id, out sb)) return 1f;
            ModifiableFloat f = sb.Data as ModifiableFloat;
            return f == null ? 1f : Mathf.Max(MinFactor, f.Value);
        }

        private static bool EnsureAccessors()
        {
            if (_getTimer != null) return true;
            if (_accessorsFailed) return false;
            try
            {
                _getTimer = AccessTools.MethodDelegate<Func<BuildProcess, float>>(
                    AccessTools.PropertyGetter(typeof(BuildProcess), "StateTimer"));
                _setTimer = AccessTools.MethodDelegate<Action<BuildProcess, float>>(
                    AccessTools.PropertySetter(typeof(BuildProcess), "StateTimer"));
                return true;
            }
            catch (Exception e)
            {
                _accessorsFailed = true;
                _getTimer = null;
                Plugin.Log.LogError("Ectype: BuildProcess.StateTimer is not reachable - " +
                                    FrameworkStats.SystemModuleConstructionSpeed + " is inert: " + e);
                return false;
            }
        }

        public static void Advance(BuildProcess process)
        {
            try
            {
                if (process == null) return;
                ConstructionState state = process.ConstructionState;
                if (state != ConstructionState.Working && state != ConstructionState.Completing) return;
                if (!(process is ConstructionProcess) && !(process is DeconstructionProcess) &&
                    !(process is UpgradeProcess)) return;

                Installer who;
                if (!_installers.TryGetValue(process, out who))
                {
                    // Cached even when no character is found, so a process is looked up once.
                    // Not the game's GetByActorId: its single-match query throws while the
                    // player list still holds a destroyed character as a match ends.
                    who = new Installer { Player = Topology.CharacterOf(process.RequesterActorId) };
                    _installers.Add(process, who);
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: " + process.Type + " started by actor " +
                                           process.RequesterActorId + " at " +
                                           FrameworkStats.SystemModuleConstructionSpeed + " " +
                                           Factor(who.Player).ToString("0.###") + ".");
                }

                float factor = Factor(who.Player);
                if (Math.Abs(factor - 1f) < 0.0001f || !EnsureAccessors()) return;
                _setTimer(process, _getTimer(process) + Time.deltaTime * (factor - 1f));
            }
            catch (Exception e)
            {
                // Run is called every frame per process, so a fault would otherwise log every
                // frame.
                if (_reportedFailure) return;
                _reportedFailure = true;
                Plugin.Log.LogError("Ectype: applying " + FrameworkStats.SystemModuleConstructionSpeed +
                                    " failed (suppressing further reports): " + e);
            }
        }

        private static bool _reportedFailure;
    }

    /// <summary>
    /// Prefix: Run adds this frame's deltaTime and then tests the current state's duration, so
    /// the extra time lands in the same frame's test. Run executes only on the master.
    /// </summary>
    [HarmonyPatch(typeof(BuildProcess), nameof(BuildProcess.Run))]
    internal static class BuildProcess_Run_Patch
    {
        private static void Prefix(BuildProcess __instance)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            BuildStats.Advance(__instance);
        }
    }
}
