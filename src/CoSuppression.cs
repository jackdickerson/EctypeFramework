using System;

using Gameplay.Perks;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Two installed classes can suppress the same vanilla tree. Suppression records one
    /// adopter per suppressed perk - the first requester in classId order - which is right
    /// for the payloads that can have only one home, but it also left every other
    /// co-suppressor unable to take over a fabricator unlock of that tree, even one the
    /// first adopter never asked for. The unlock was then skipped as "someone else's".
    ///
    /// The safety property adoption rests on is unchanged: a class takes over only a
    /// payload that its OWN suppression orphaned. This answers that question per class
    /// rather than per first requester. Two classes asking for the same unlock still
    /// resolve first come, first served: once one has repointed it, the unlock's perk is
    /// that class's own and no longer a suppressed one.
    /// </summary>
    internal static class CoSuppression
    {
        /// <summary>
        /// True when <paramref name="rec"/> is active, lists in its manifest a vanilla tree
        /// that is currently suppressed, and that tree holds <paramref name="perkGuid"/>.
        /// False until suppression has resolved.
        /// </summary>
        internal static bool Suppresses(ClassRecord rec, GUIDUnion perkGuid)
        {
            if (rec == null || perkGuid.IsNull || !Suppression.Resolved) return false;
            if (Suppression.IsClassSuppressed(rec.Manifest.classId)) return false;
            if (!Suppression.IsScrubbedPerk(perkGuid)) return false;

            PerkTreeContainer tc = PerkTreeContainer.Instance;
            if (tc == null) return false;
            foreach (string hex in rec.Manifest.suppress)
            {
                try
                {
                    GUIDUnion tree = new GUIDUnion(hex);
                    if (!Suppression.IsSuppressedTree(tree) || !tc.HasItem(tree)) continue;
                    PerkTreeDef def = tc.GetAssetDefById(tree, true);
                    if (def == null || def.Asset == null || def.Asset.Perks == null) continue;
                    foreach (PerkBuffRef pref in def.Asset.Perks)
                        if (pref != null && pref.AssetGuid.Equals(perkGuid)) return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Ectype: reading suppressed tree " + hex + " for '" +
                                          rec.Manifest.classId + "' failed: " + e.Message);
                }
            }
            return false;
        }
    }
}
