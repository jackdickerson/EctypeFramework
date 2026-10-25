using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Cloud;
using CG.Profile;
using HarmonyLib;
using Photon.Pun;

namespace Ectype
{
    /// <summary>
    /// Keeps a modded session out of the player's progression, with or without VoidManager.
    /// The game tags a modded room (room property <c>R_Mod</c>) but never reads the tag to stop
    /// anything, and VoidManager's own progression switch reacts only to a mod calling its
    /// <c>DisableProgression</c>, never to the tag; progression and achievements keep being
    /// written unless a mod stops them.
    ///
    /// The writes that carry progression:
    ///  * <c>CloudSyncController.Write</c>, which queues every cloud profile entry;
    ///  * <c>CloudLocalProfile.OnQuit</c> and <c>LocalPlayerProfile.OnQuit</c>, which save the
    ///    profile on exit;
    ///  * Steamworks' <c>SteamUserStats.SetStat</c> (both overloads), <c>SetAchievement</c>,
    ///    <c>IndicateAchievementProgress</c> and <c>StoreStats</c>.
    /// Each gets a prefix that skips the call once the guard is engaged. A skipped Steam call
    /// returns false, which the game treats as a failed Steam call.
    ///
    /// The guard engages the first time this client is in a room carrying the modded tag, and
    /// stays engaged until the game restarts: the profile held in memory already carries what
    /// that session earned, so a later write, in a vanilla room or on exit, would save it.
    ///
    /// VoidManager prefixes some of the same methods for its switch. Each prefix only skips the
    /// call, so the two coexist: the write runs only when neither blocks it.
    /// </summary>
    internal static class ProgressionGuard
    {
        private const float PollInterval = 1f;
        private static bool _installed;
        private static bool _engaged;
        private static float _nextPoll;

        /// <summary>True once a modded room has been joined this process.</summary>
        internal static bool Engaged { get { return _engaged; } }

        internal static string Status
        {
            get
            {
                return !_installed ? "not installed (every hook failed)"
                     : _engaged ? "progression and achievements blocked until restart"
                     : "armed (engages in a modded room)";
            }
        }

        /// <summary>Patches the write methods. Called once from the plugin's Awake. A missing
        /// method is logged and the rest still patch.</summary>
        internal static void Install(Harmony harmony)
        {
            HarmonyMethod skip = new HarmonyMethod(typeof(ProgressionGuard), nameof(SkipWhileEngaged));
            HarmonyMethod skipBool = new HarmonyMethod(typeof(ProgressionGuard), nameof(SkipBoolWhileEngaged));
            List<string> missing = new List<string>();
            int patched = 0;

            Type steam = Type.GetType("Steamworks.SteamUserStats, com.rlabrecque.steamworks.net");
            if (steam == null)
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "com.rlabrecque.steamworks.net")
                    { steam = a.GetType("Steamworks.SteamUserStats"); break; }
            Target[] targets =
            {
                new Target(typeof(CloudSyncController), "Write",
                           new[] { typeof(string), typeof(string), typeof(bool), typeof(Action<bool>) }, skip),
                new Target(typeof(CloudLocalProfile), "OnQuit", Type.EmptyTypes, skip),
                new Target(typeof(LocalPlayerProfile), "OnQuit", Type.EmptyTypes, skip),
                new Target(steam, "SetStat", new[] { typeof(string), typeof(int) }, skipBool),
                new Target(steam, "SetStat", new[] { typeof(string), typeof(float) }, skipBool),
                new Target(steam, "SetAchievement", new[] { typeof(string) }, skipBool),
                new Target(steam, "IndicateAchievementProgress",
                           new[] { typeof(string), typeof(uint), typeof(uint) }, skipBool),
                new Target(steam, "StoreStats", Type.EmptyTypes, skipBool),
            };
            foreach (Target t in targets)
            {
                MethodInfo m = t.Type != null ? AccessTools.Method(t.Type, t.Name, t.Args) : null;
                if (m == null)
                {
                    missing.Add((t.Type != null ? t.Type.Name : "SteamUserStats") + "." + t.Name);
                    continue;
                }
                try { harmony.Patch(m, prefix: t.Prefix); patched++; }
                catch (Exception e)
                {
                    missing.Add(t.Name);
                    Plugin.Log.LogError("Ectype: progression guard could not patch " + t.Name + ": " + e);
                }
            }
            _installed = patched > 0;
            if (missing.Count > 0)
                Plugin.Log.LogWarning("Ectype: progression guard is missing " +
                                      string.Join(", ", missing.ToArray()) + " (game update?); " +
                                      "those writes are not blocked in a modded session.");
        }

        /// <summary>Driver step: engages the guard in a room carrying the modded tag.</summary>
        internal static void Poll()
        {
            if (!_installed || _engaged) return;
            if (UnityEngine.Time.unscaledTime < _nextPoll) return;
            _nextPoll = UnityEngine.Time.unscaledTime + PollInterval;
            if (!PhotonNetwork.InRoom ||
                !VoidManagerBridge.TaggedModded(PhotonNetwork.CurrentRoom.CustomProperties)) return;
            _engaged = true;
            Plugin.Log.LogInfo("Ectype: progression guard engaged: this room is tagged modded, so " +
                               "cloud profile writes, Steam stats and achievements are blocked " +
                               "until the game restarts.");
        }

        private static bool SkipWhileEngaged()
        {
            return !_engaged;
        }

        private static bool SkipBoolWhileEngaged(ref bool __result)
        {
            if (!_engaged) return true;
            __result = false;
            return false;
        }

        private struct Target
        {
            internal readonly Type Type;
            internal readonly string Name;
            internal readonly Type[] Args;
            internal readonly HarmonyMethod Prefix;

            internal Target(Type type, string name, Type[] args, HarmonyMethod prefix)
            {
                Type = type; Name = name; Args = args; Prefix = prefix;
            }
        }
    }
}
