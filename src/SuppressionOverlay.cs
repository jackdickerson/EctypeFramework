using System;
using System.Collections.Generic;
using HarmonyLib;

using CG.Client.UserData;
using CG.Profile;
using Gameplay.Perks;
using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Character.Abilities;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// The profile half of suppression. A suppressed perk stays in the saved loadout, and the
    /// places the game asks the profile what a player holds are told it is absent: the
    /// spent-point count, role-ability selection, and PlayerPerk fabricator unlocks. Stats
    /// read the published PBL copy instead, which the ReApplyPerks prefix already filters.
    ///
    /// The tree UI's own per-perk reads (PerkBuffExtensions.GetBuffLevel and friends) are left
    /// alone: a suppressed tree is not in the document, so nothing renders them.
    /// </summary>
    internal static class SuppressionOverlay
    {
        private static bool _reportedSyncFailure;

        internal static bool IsSuppressed(ClassAbility ability)
        {
            PerkBuff perk = Refl.GetField<PerkBuff>(ability, "AbilityPerk", null);
            return perk != null && Suppression.IsSuppressedPerk(perk.ContainerGuid);
        }

        /// <summary>
        /// Bring each local role ability's selection in line with the profile after a
        /// suppression change, flipping only the abilities whose answer changed.
        ///
        /// A select runs OnClassAbilitySelected, which restarts the cooldown
        /// (GoOnCooldown(wasInUse: false)) - a cost the game only pays on a real perk pick -
        /// so the running cooldown is carried across it.
        /// </summary>
        internal static void SyncLocalSelection(UltimateCharacterLocomotion loco)
        {
            if (loco == null || loco.Abilities == null) return;
            IPlayerPerkData data = PlayerProfile.PerkData;
            if (data == null || data.PerkBuffLevels == null) return;

            foreach (Ability a in loco.Abilities)
            {
                ClassAbility ca = a as ClassAbility;
                if (ca == null) continue;
                try
                {
                    PerkBuff perk = Refl.GetField<PerkBuff>(ca, "AbilityPerk", null);
                    if (perk == null) continue;
                    int level;
                    bool want = data.PerkBuffLevels.TryGetValue(perk, out level) && level > 0 &&
                                !Suppression.IsSuppressedPerk(perk.ContainerGuid);
                    if (want == Refl.GetField(ca, "IsSelectedAbility", false)) continue;

                    float cooldown = ca.CurrentCooldown;
                    ca.SetAsSelectedAbility(want);
                    if (want) ca.RefundCooldown(cooldown);
                }
                catch (Exception e)
                {
                    if (_reportedSyncFailure) continue;
                    _reportedSyncFailure = true;
                    Plugin.Log.LogError("Ectype: syncing role-ability selection after a " +
                                        "suppression change failed (reported once): " + e);
                }
            }
        }
    }

    [HarmonyPatch(typeof(PlayerProfile), "UsedPerkPoints", MethodType.Getter)]
    internal static class Patch_PlayerProfile_UsedPerkPoints
    {
        private static bool _reportedFailure;

        [HarmonyPostfix]
        private static void Postfix(ref int __result)
        {
            try
            {
                IPlayerPerkData data = PlayerProfile.PerkData;
                if (data == null || data.PerkBuffLevels == null) return;
                foreach (KeyValuePair<PerkBuff, int> kv in data.PerkBuffLevels)
                    if (kv.Key != null && kv.Value > 0 &&
                        Suppression.IsSuppressedPerk(kv.Key.ContainerGuid))
                        __result -= kv.Value;
            }
            catch (Exception e)
            {
                if (_reportedFailure) return;
                _reportedFailure = true;
                Plugin.Log.LogError("Ectype: suppressed-perk point refund failed " +
                                    "(reported once): " + e);
            }
        }
    }

    /// <summary>
    /// A suppressed ability is never selected, whoever asks - Start, the perk-reload
    /// re-check, or a mod. Turning the request into a deselect, rather than deselecting after
    /// the game selected, matters: every select-then-deselect pair restarts the cooldown and
    /// republishes OnLocalPlayerSelectedClassAbility, and the re-check runs on every
    /// crewmate's perk change.
    /// </summary>
    [HarmonyPatch(typeof(ClassAbility), "SetAsSelectedAbility")]
    internal static class Patch_ClassAbility_SetAsSelected_Suppressed
    {
        private static bool _reportedFailure;

        [HarmonyPrefix]
        private static void Prefix(ClassAbility __instance, ref bool __0)
        {
            if (!__0) return;
            try
            {
                if (!SuppressionOverlay.IsSuppressed(__instance)) return;
                __0 = false;
                // Start's own not-owned state is disabled, which a no-op deselect would skip.
                if (!Refl.GetField(__instance, "IsSelectedAbility", false))
                    __instance.Enabled = false;
            }
            catch (Exception e)
            {
                if (_reportedFailure) return;
                _reportedFailure = true;
                Plugin.Log.LogError("Ectype: suppressed role-ability select gate failed " +
                                    "(reported once): " + e);
            }
        }
    }

    [HarmonyPatch(typeof(UnlockContainer), "IsUnlocked", new Type[] { typeof(GUIDUnion) })]
    internal static class Patch_UnlockContainer_IsUnlocked_Suppressed
    {
        private static bool _reportedFailure;

        [HarmonyPostfix]
        private static void Postfix(UnlockContainer __instance, GUIDUnion __0, ref bool __result)
        {
            if (!__result) return;
            try
            {
                UnlockItemDef def;
                if (!__instance.TryGetByGuid(__0, out def) || def == null) return;
                UnlockOptions opts = def.UnlockOptions;
                if (opts.UnlockCriteria != UnlockCriteriaType.PlayerPerk ||
                    opts.RequiredPerk == null) return;
                if (Suppression.IsSuppressedPerk(opts.RequiredPerk.AssetGuid)) __result = false;
            }
            catch (Exception e)
            {
                if (_reportedFailure) return;
                _reportedFailure = true;
                Plugin.Log.LogError("Ectype: suppressed-perk unlock check failed " +
                                    "(reported once): " + e);
            }
        }
    }
}
