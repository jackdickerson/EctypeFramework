using System;
using System.Collections.Generic;
using HarmonyLib;

using Gameplay.Perks;
using ResourceAssets;
using UI.MainHUD;

namespace Ectype
{
    /// <summary>
    /// GetPlayerRole tallies into a dictionary literal holding exactly the four vanilla trees,
    /// so a perk from a fifth tree is a KeyNotFoundException. This prefix runs the same
    /// algorithm seeded from the registered PerkTreeDefs instead, and on internal failure
    /// reports None rather than falling back to the original, which would throw into five
    /// callers.
    ///
    /// A framework tree at ordinal n yields (EPlayerRole)(n+1), whose USS class does not
    /// exist - a blank lobby role icon, cosmetic until real icons are injected.
    /// </summary>
    [HarmonyPatch(typeof(PlayerDataHelper), "GetPlayerRole",
                  typeof(Dictionary<GUIDUnion, int>))]
    internal static class Patch_PlayerDataHelper_GetPlayerRole
    {
        private static bool _reportedFailure;

        /// <summary>Each def's tree enum, read once by reflection. The master runs this for every
        /// room member each time any character's perks reapply, so the uncached read cost grew
        /// with the square of the player count.</summary>
        private static readonly Dictionary<PerkTreeDef, EPerkTree> TreeOf =
            new Dictionary<PerkTreeDef, EPerkTree>();

        private static EPerkTree TreeEnum(PerkTreeDef def)
        {
            EPerkTree tree;
            if (TreeOf.TryGetValue(def, out tree)) return tree;
            tree = Refl.GetField(def, "perkTree", (EPerkTree)0);
            TreeOf[def] = tree;
            return tree;
        }

        [HarmonyPrefix]
        private static bool Prefix(Dictionary<GUIDUnion, int> __0, ref EPlayerRole __result)
        {
            try
            {
                Dictionary<EPerkTree, int> tally = new Dictionary<EPerkTree, int>();
                PerkTreeContainer container = PerkTreeContainer.Instance;
                if (container == null || __0 == null) { __result = EPlayerRole.None; return false; }

                foreach (PerkTreeRef treeRef in container.GetPerkRefs())
                {
                    if (treeRef == null) continue;
                    // A suppressed tree no longer exists at the terminal; perks still held
                    // in it must not make the player count as that role either.
                    if (Suppression.IsSuppressedTree(treeRef.AssetGuid)) continue;
                    PerkTreeDef def = container.GetAssetDefById(treeRef.AssetGuid, true);
                    if (def == null || def.Asset == null || def.Asset.Perks == null) continue;

                    EPerkTree tree = TreeEnum(def);
                    if (!tally.ContainsKey(tree)) tally[tree] = 0;

                    foreach (PerkBuffRef perk in def.Asset.Perks)
                    {
                        int level;
                        if (perk != null && __0.TryGetValue(perk.AssetGuid, out level))
                            tally[tree] += level;
                    }
                }

                // Vanilla argmax: strictly-greater, so the first maximum wins ties in
                // enumeration order - preserved by using the same tally-then-scan shape.
                EPerkTree best = (EPerkTree)0;
                int bestLevels = 0;
                foreach (KeyValuePair<EPerkTree, int> kv in tally)
                    if (kv.Value > bestLevels) { best = kv.Key; bestLevels = kv.Value; }

                __result = bestLevels < 1 ? EPlayerRole.None : (EPlayerRole)((int)best + 1);
                return false;
            }
            catch (Exception e)
            {
                if (!_reportedFailure)
                {
                    _reportedFailure = true;
                    Plugin.Log.LogError("Ectype: GetPlayerRole replacement failed, " +
                                        "reporting None: " + e);
                }
                __result = EPlayerRole.None;
                return false;
            }
        }
    }

    /// <summary>
    /// ReApplyPerks dereferences GetAssetDefById(guid).Asset, which is null for an unknown
    /// GUID, and indexes asset.Levels[level - 1] unchecked; either throws after ResetPerks()
    /// has already run - wiping that player's perks on this client and freezing ability
    /// selection. The prefix drops unresolvable entries first, applying vanilla's own
    /// load-time drop semantics to the path that forgot them, and clamps a level above the
    /// perk's current Levels.Count (a perk that lost levels between builds) to its top level.
    /// A perk with no levels at all counts as unresolvable. The clamp is a backstop: the
    /// dictionary is this client's deserialized copy of the owner's PBL, and the refund itself
    /// happens on the owner at load (Patch_PerkLoadout_Ctor). It still catches an owner on an
    /// older framework build.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPerkLoader), "ReApplyPerks")]
    internal static class Patch_PlayerPerkLoader_ReApplyPerks
    {
        private static readonly HashSet<string> _reported = new HashSet<string>();

        [HarmonyPrefix]
        private static void Prefix(PlayerPerkLoader __instance)
        {
            try
            {
                Dictionary<GUIDUnion, int> levels = __instance.PerkBuffLevels;
                PerkBuffContainer container = PerkBuffContainer.Instance;
                if (levels == null || container == null) return;

                List<GUIDUnion> drop = null;
                List<KeyValuePair<GUIDUnion, int>> clamp = null;
                foreach (KeyValuePair<GUIDUnion, int> kv in levels)
                {
                    if (kv.Key.IsNull || kv.Value <= 0) continue;
                    PerkBuffDef def;
                    // Suppressed perks resolve fine - they are dropped on purpose, so their
                    // effects never apply while their tree does not exist.
                    if (!container.TryGetByGuid(kv.Key, out def) || def == null ||
                        def.Asset == null || def.Asset.Levels == null ||
                        def.Asset.Levels.Count == 0 || Suppression.IsSuppressedPerk(kv.Key))
                        (drop = drop ?? new List<GUIDUnion>()).Add(kv.Key);
                    else if (kv.Value > def.Asset.Levels.Count)
                        (clamp = clamp ?? new List<KeyValuePair<GUIDUnion, int>>())
                            .Add(new KeyValuePair<GUIDUnion, int>(kv.Key, def.Asset.Levels.Count));
                }

                if (clamp != null)
                    for (int i = 0; i < clamp.Count; i++)
                    {
                        GUIDUnion g = clamp[i].Key;
                        int saved = levels[g];
                        levels[g] = clamp[i].Value;
                        if (_reported.Add("clamp:" + g.AsHex()))
                            Plugin.Log.LogWarning("Ectype: perk GUID " + g.AsHex() + " held at " +
                                                  "level " + saved + " but has " + clamp[i].Value +
                                                  " level(s) - applying level " + clamp[i].Value +
                                                  " (would have thrown and wiped this player's " +
                                                  "perks).");
                    }
                if (drop == null) return;

                for (int i = 0; i < drop.Count; i++)
                {
                    levels.Remove(drop[i]);
                    if (_reported.Add(drop[i].AsHex()))
                        Plugin.Log.LogWarning("Ectype: dropped perk GUID " + drop[i].AsHex() +
                                              " before ReApplyPerks (" +
                                              (Suppression.IsSuppressedPerk(drop[i])
                                               ? "suppressed"
                                               : "unresolvable - would have thrown and wiped " +
                                                 "this player's perks") + ").");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: ReApplyPerks prefix failed: " + e);
            }
        }

        /// <summary>
        /// Catch-up for ability adoption: the install-time sweep runs at character spawn and
        /// skips any role whose registration has not settled, whereas this runs on every
        /// perk change - the moment the answer would change anyway.
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix()
        {
            try { Adoption.ReSweep(); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: adoption re-sweep failed: " + e); }
        }
    }

    /// <summary>
    /// PerkLoadout's constructor is where every persisted loadout resolves perk GUIDs and
    /// silently drops what does not resolve. Profile load and container-gated registration
    /// race at startup, so forcing registration from a prefix here settles the ordering and a
    /// saved selection of a framework perk survives.
    ///
    /// Also the load half of suppression's scrub: saved selections in a suppressed vanilla tree
    /// are removed from the incoming dictionary, which is exactly vanilla's degrade path for an
    /// unknown GUID - the points come back as unspent and the next profile write prunes. A
    /// suppressed framework role is never scrubbed (SuppressionOverlay).
    /// (If suppression is not yet resolved on the first loadouts, the ReApplyPerks drop
    /// still keeps the effects from applying, and a later load completes the scrub.)
    ///
    /// Also the refund for a perk that lost levels between builds: a saved level above the
    /// perk's current Levels.Count is lowered to it. Spent points are the sum of held levels
    /// (PlayerProfile.UsedPerkPoints), so the difference comes back as unspent the same way.
    /// Only the owner builds PerkLoadouts (local and cloud profile readers); the PBL it then
    /// publishes carries the lowered level, and the next perk write on that loadout persists it.
    /// </summary>
    [HarmonyPatch(typeof(CG.Profile.PerkLoadout), MethodType.Constructor,
                  new Type[] { typeof(Dictionary<GUIDUnion, int>) })]
    internal static class Patch_PerkLoadout_Ctor
    {
        private static readonly HashSet<string> _refundReported = new HashSet<string>();

        [HarmonyPrefix]
        private static void Prefix(Dictionary<GUIDUnion, int> __0)
        {
            try
            {
                EctypeAssets.EnsureRegistered();
                if (__0 == null) return;
                PerkBuffContainer container = PerkBuffContainer.Instance;
                List<GUIDUnion> drop = null;
                List<KeyValuePair<GUIDUnion, PerkBuff>> clamp = null;
                foreach (KeyValuePair<GUIDUnion, int> kv in __0)
                {
                    if (Suppression.IsScrubbedPerk(kv.Key))
                    {
                        (drop = drop ?? new List<GUIDUnion>()).Add(kv.Key);
                        continue;
                    }
                    PerkBuffDef def;
                    // A perk with no levels is left to the ReApplyPerks drop: lowering it to
                    // zero would be a removal, not a refund of lost levels.
                    if (container != null && kv.Value > 1 &&
                        container.TryGetByGuid(kv.Key, out def) && def != null &&
                        def.Asset != null && def.Asset.Levels != null &&
                        def.Asset.Levels.Count > 0 && kv.Value > def.Asset.Levels.Count)
                        (clamp = clamp ?? new List<KeyValuePair<GUIDUnion, PerkBuff>>())
                            .Add(new KeyValuePair<GUIDUnion, PerkBuff>(kv.Key, def.Asset));
                }

                if (clamp != null)
                    foreach (KeyValuePair<GUIDUnion, PerkBuff> c in clamp)
                    {
                        int saved = __0[c.Key], top = c.Value.Levels.Count;
                        __0[c.Key] = top;
                        if (_refundReported.Add(c.Key.AsHex()))
                            Plugin.Log.LogInfo("Ectype: saved perk " + c.Value.name + " (" +
                                               c.Key.AsHex() + ") held at level " + saved +
                                               " but has " + top + " - lowered to " + top + ", " +
                                               (saved - top) + " point(s) return as unspent.");
                    }

                if (drop == null) return;
                foreach (GUIDUnion g in drop)
                {
                    __0.Remove(g);
                    if (Suppression.ReportScrub(g.AsHex()))
                        Plugin.Log.LogInfo("Ectype: scrubbed saved perk " + g.AsHex() +
                                           " from a loadout (its tree is suppressed; the " +
                                           "point returns as unspent).");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: PerkLoadout ctor prefix failed: " + e);
            }
        }
    }
}
