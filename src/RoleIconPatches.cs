using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>
    /// The crew HUD and the lobby room list draw a player's role icon purely from USS: the
    /// element gets the class "coop-buddy-role-state__&lt;role&gt;" and a stylesheet rule
    /// supplies the image. A framework role's rule lives in the per-role sheet UssPalette
    /// builds, and a sheet only applies to the panel it is attached to - so these two seams,
    /// the only places a role icon is assigned, attach it to their own panel first.
    /// </summary>
    internal static class RoleIconTargets
    {
        internal static MethodInfo Resolve(string type, string method, string consequence)
        {
            try
            {
                Type t = AccessTools.TypeByName(type);
                MethodInfo m = t != null ? AccessTools.Method(t, method) : null;
                if (m == null)
                    Plugin.Log.LogError("Ectype: " + type + "." + method + " not found - " +
                                        consequence + " (game update?).");
                return m;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: resolving " + type + "." + method + " failed - " +
                                    consequence + ": " + e);
                return null;
            }
        }

        internal static bool IsFrameworkRole(EPlayerRole role)
        {
            return (int)role > (int)EPlayerRole.Scavenger;
        }
    }

    [HarmonyPatch]
    internal static class Patch_BasePlayerHUD_ChangeRole
    {
        // Yield nothing on a miss: an empty list patches nothing, a null target aborts PatchAll.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo m = RoleIconTargets.Resolve("BasePlayerHUD", "ChangeRole",
                "framework roles show no role icon on the crew HUD");
            if (m != null) yield return m;
        }

        private static void Prefix(object __instance, EPlayerRole __0)
        {
            try
            {
                if (!RoleIconTargets.IsFrameworkRole(__0)) return;
                VisualElement roleState =
                    Traverse.Create(__instance).Field("roleState").GetValue<VisualElement>();
                if (roleState != null) UssPalette.EnsureInstalled(roleState);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: HUD role icon install failed: " + e);
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_MatchmakingRoom_AddPlayerRoleIcons
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo m = RoleIconTargets.Resolve("MatchmakingRoom", "AddPlayerRoleIcons",
                "framework roles show no role icon in the lobby room list");
            if (m != null) yield return m;
        }

        private static void Prefix(object __instance, Dictionary<string, VisualElement> __0)
        {
            try
            {
                List<EPlayerRole> roles =
                    Traverse.Create(__instance).Field("PlayerRoles").GetValue<List<EPlayerRole>>();
                if (roles == null || !roles.Exists(RoleIconTargets.IsFrameworkRole)) return;
                VisualElement container;
                if (__0 != null && __0.TryGetValue("PlayerRolesContainer", out container) &&
                    container != null)
                    UssPalette.EnsureInstalled(container);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: lobby role icon install failed: " + e);
            }
        }
    }
}
