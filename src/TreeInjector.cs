using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.UIElements;

using CG.Profile;
using Gameplay.Perks;
using UI.MainHUD;
using UI.Token;

namespace Ectype
{
    /// <summary>
    /// Builds one PerkTreeVE per registered class - named by its tree ordinal ("4", "5", ...)
    /// - into the document BEFORE PerkTreeController's constructor queries it, in both the
    /// token terminal and the codex. Registration closes at the first ctor run: an ordinal
    /// allocated after this point could differ between clients, so late discovery is refused.
    ///
    /// The element comes from the PRISTINE AUTHORED TEMPLATE, never a clone of the live
    /// document, because a live tree carries runtime-mutated state a clone would capture.
    /// What stays mod-owned: the name, the PerkTree enum value, occupancy dressing, and the
    /// labels.
    ///
    /// No scrolling yet: with more than four trees total the row overflows off-layout. The
    /// presentation work item designs for arbitrary count; the terminals stay functional.
    ///
    /// If injection cannot produce an element, the published def must not reach the ctor (it
    /// would Q a null and kill the terminal), so the fallback hands the ctor a filtered def
    /// collection - the terminal degrades by exactly the classes that failed.
    /// </summary>
    [HarmonyPatch(typeof(PerkTreeController), MethodType.Constructor,
                  new Type[] { typeof(IReadOnlyCollection<ResourceAssets.PerkTreeDef>),
                               typeof(VisualElement), typeof(TokenTerminalEvents),
                               typeof(ILocalizationProvider),
                               typeof(Client.Player.Interactions.InteractionInfo),
                               typeof(bool) })]
    internal static class Patch_PerkTreeController_Ctor
    {
        // POSITIONAL parameter binding throughout (__0, __1): named binding would make a
        // game-side parameter rename a hard PatchAll abort at load.
        [HarmonyPrefix]
        private static void Prefix(ref IReadOnlyCollection<ResourceAssets.PerkTreeDef> __0,
                                   VisualElement __1)
        {
            try { PrefixInner(ref __0, __1); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: tree controller prefix failed: " + e); }
        }

        private static void PrefixInner(ref IReadOnlyCollection<ResourceAssets.PerkTreeDef> __0,
                                        VisualElement __1)
        {
            VisualElement root = __1;
            ClassRegistry.RegistrationClosed = true;
            // Belt for the scrub paths: registration (and with it suppression resolution)
            // normally settled long before the first terminal build.
            try { Suppression.Resolve(); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: suppression resolution failed: " + e); }
            // Both terminal documents get the named-color sheets, so any USS consumer of
            // the role palette (trees, icons) resolves the framework variables here too.
            try { if (root != null) UssPalette.EnsureInstalled(root); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: palette install failed: " + e); }

            List<ClassRecord> missing = null;
            foreach (ClassRecord rec in ClassRegistry.Classes)
            {
                if (!rec.TreeDefPublished) continue;
                // Suppressed (by the host or another class's suppressClasses): skip injection
                // rather than build-then-hide, so an element with no def reaching the ctor never
                // exists even transiently; routed through the same `missing` degrade path as a
                // failed build.
                if (Suppression.IsClassSuppressed(rec.Manifest.classId))
                {
                    rec.ElementInjected = false;
                    (missing = missing ?? new List<ClassRecord>()).Add(rec);
                    continue;
                }
                try
                {
                    rec.ElementInjected = Inject(root, rec);
                }
                catch (Exception e)
                {
                    rec.ElementInjected = false;
                    Plugin.Log.LogError("Ectype: tree VE injection for '" +
                                        rec.Manifest.className + "' failed: " + e);
                }
                if (!rec.ElementInjected)
                    (missing = missing ?? new List<ClassRecord>()).Add(rec);
            }

            // Suppressed vanilla trees: take their elements out of the document (freeing
            // their layout slot) - safe because their defs are filtered below, so the ctor
            // never Qs them.
            try
            {
                if (root != null)
                {
                    foreach (string name in Suppression.TreeElementNames)
                    {
                        PerkTreeVE gone = root.Q<PerkTreeVE>(name);
                        if (gone != null) gone.RemoveFromHierarchy();
                    }
                    // Suppressed ectype classes: the skip above only stops a NEW element
                    // appearing, so remove any a previous ctor call already injected.
                    foreach (ClassRecord rec in ClassRegistry.Classes)
                    {
                        if (!Suppression.IsClassSuppressed(rec.Manifest.classId)) continue;
                        PerkTreeVE gone = root.Q<PerkTreeVE>(rec.Ordinal.ToString());
                        if (gone != null) gone.RemoveFromHierarchy();
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: suppressed-tree element removal failed: " + e);
            }

            // Two reasons a def must not reach the unchecked Q: its class's element could
            // not be injected (degrade), or its tree is suppressed by a manifest (intent).
            if (missing != null || Suppression.HasSuppressedTrees)
                try
                {
                    if (__0 != null)
                    {
                        List<ResourceAssets.PerkTreeDef> filtered =
                            new List<ResourceAssets.PerkTreeDef>();
                        foreach (ResourceAssets.PerkTreeDef def in __0)
                        {
                            bool drop = def != null && def.Ref != null &&
                                        Suppression.IsSuppressedTree(def.Ref.AssetGuid);
                            if (!drop && missing != null)
                                foreach (ClassRecord rec in missing)
                                    if (ReferenceEquals(def, rec.TreeDef)) { drop = true; break; }
                            if (!drop) filtered.Add(def);
                        }
                        if (filtered.Count != ((ICollection<ResourceAssets.PerkTreeDef>)__0).Count)
                        {
                            __0 = filtered;
                            if (missing != null)
                                foreach (ClassRecord rec in missing)
                                    Plugin.Log.LogError("Ectype: tree element for '" +
                                                        rec.Manifest.className + "' missing - its " +
                                                        "PerkTreeDef was filtered out of this build so " +
                                                        "the terminal survives without it.");
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: def filter fallback failed: " + e);
                }

            // W3 presentation: wrap the row when over four trees survive; always log the
            // resolved geometry (both are per-document decisions - see Presentation).
            try
            {
                ICollection<ResourceAssets.PerkTreeDef> defs =
                    __0 as ICollection<ResourceAssets.PerkTreeDef>;
                Presentation.Apply(root, defs != null ? defs.Count : 0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: presentation apply failed: " + e);
            }
        }

        private static bool Inject(VisualElement root, ClassRecord rec)
        {
            if (root == null) return false;
            string treeName = rec.Ordinal.ToString();
            // TYPED lookups throughout: the document holds unrelated elements named "4".
            if (root.Q<PerkTreeVE>(treeName) != null) return true;   // idempotent

            PerkTreeVE anchor = root.Q<PerkTreeVE>("Scavenger");
            if (anchor == null || anchor.parent == null)
            {
                Plugin.Log.LogWarning("Ectype: no 'Scavenger' PerkTreeVE in this document - " +
                                      "cannot place tree '" + treeName + "'.");
                return false;
            }

            PerkTreeVE tree = InstantiatePristine(root);
            if (tree == null) return false;

            tree.name = treeName;
            // A fresh instantiation carries the authored PerkTreeAttr (Scavenger); left
            // unset, our hero is filed under the donor's enum and never credited to us.
            tree.PerkTree.Value = (EPerkTree)rec.Ordinal;
            DressCells(tree, rec);

            Label className = tree.Q<Label>("ClassName");
            if (className != null)
                className.text = rec.Manifest.className.ToUpperInvariant();
            Label spent = tree.Q<Label>("SpentPoints");
            if (spent != null) spent.text = "0";

            // Insert in ordinal order after the last tree already present, so multiple
            // classes land as 4, 5, 6... regardless of injection order.
            VisualElement parent = anchor.parent;
            int insertAt = parent.IndexOf(anchor) + 1;
            for (int o = EctypePluginInfo.FIRST_TREE_ORDINAL; o < rec.Ordinal; o++)
            {
                PerkTreeVE prior = root.Q<PerkTreeVE>(o.ToString());
                if (prior != null && prior.parent == parent)
                    insertAt = parent.IndexOf(prior) + 1;
            }
            parent.Insert(insertAt, tree);
            // No accent code here: colors resolve through the USS palette, as a stock tree's do.
            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: PerkTreeVE '" + treeName + "' (" +
                                                   rec.Manifest.className + ") instantiated from the authored " +
                                                   "template.");
            return true;
        }

        /// <summary>
        /// A pristine authored Scavenger tree: instantiate the owning UIDocument's
        /// visualTreeAsset into a throwaway copy and lift the subtree out. Nothing has run
        /// against a panel yet, so no runtime state can leak; AttachToPanel setup fires when
        /// the subtree is inserted into the live document.
        /// </summary>
        private static PerkTreeVE InstantiatePristine(VisualElement root)
        {
            UIDocument owner = null;
            foreach (UIDocument doc in UnityEngine.Object.FindObjectsOfType<UIDocument>())
            {
                VisualElement docRoot = doc.rootVisualElement;
                if (docRoot != null && (docRoot == root || docRoot.Contains(root)))
                {
                    owner = doc;
                    break;
                }
            }
            if (owner == null || owner.visualTreeAsset == null)
            {
                Plugin.Log.LogWarning("Ectype: could not resolve the owning UIDocument (or " +
                                      "its visualTreeAsset) for this root - cannot " +
                                      "instantiate a pristine tree.");
                return null;
            }

            TemplateContainer fresh = owner.visualTreeAsset.Instantiate();
            PerkTreeVE pristine = fresh.Q<PerkTreeVE>("Scavenger");
            if (pristine == null)
            {
                Plugin.Log.LogWarning("Ectype: no 'Scavenger' PerkTreeVE in a fresh " +
                                      "instantiation of " + owner.visualTreeAsset.name + ".");
                return null;
            }
            pristine.RemoveFromHierarchy();   // detach from the throwaway copy
            return pristine;
        }

        /// <summary>
        /// The authored empty-cell classes reflect the DONOR's occupancy and nothing at
        /// runtime touches them, so retoggle them per cell to THIS class's occupancy:
        /// filled cells are left bare on purpose, because TreeReady's Init/Update owns
        /// their dressing and will fight anything applied here.
        /// </summary>
        private static void DressCells(PerkTreeVE tree, ClassRecord rec)
        {
            HashSet<long> occupied = new HashSet<long>();
            occupied.Add(0);   // hero at (0,0)
            foreach (PerkSpec s in rec.Manifest.perks)
                occupied.Add((long)s.row << 8 | (uint)s.col);

            List<PerkLayerVE> layers = tree.Query<PerkLayerVE>().ToList();
            for (int r = 0; r < layers.Count; r++)
            {
                int c = 0;
                foreach (VisualElement child in layers[r].Children())
                {
                    PerkBuffItemVE cell = child as PerkBuffItemVE;
                    if (cell == null) continue;
                    bool empty = !occupied.Contains((long)r << 8 | (uint)c);
                    cell.EnableInClassList("perktree-perk-empty", empty);
                    cell.EnableInClassList("perk-type__normal", empty);
                    cell.EnableInClassList("perk-state__unavailable", empty);
                    c++;
                }
            }
        }
    }

    /// <summary>
    /// TokenTerminal.UpdateLoadoutButtons tints each loadout slot by toggling USS classes
    /// "loadout-role__&lt;role&gt;" for exactly the four vanilla roles, so a loadout whose role
    /// is a framework class matches none of them and renders untinted. UssPalette supplies
    /// the missing selectors in patched sheet clones, which lets this postfix toggle a
    /// framework class's role class exactly as the game toggles a vanilla one - no inline
    /// writes, so nothing has to be cleared or re-asserted.
    /// </summary>
    [HarmonyPatch(typeof(TokenTerminal), "UpdateLoadoutButtons")]
    internal static class Patch_TokenTerminal_UpdateLoadoutButtons
    {
        [HarmonyPostfix]
        private static void Postfix(TokenTerminal __instance)
        {
            try
            {
                RadioButtonGroup group =
                    Refl.GetField<RadioButtonGroup>(__instance, "loadoutSelection", null);
                List<PerkLoadout> loadouts = PlayerProfile.PerkData.Loadouts;
                if (group == null || loadouts == null) return;

                // The role selectors must be on the panel before the toggles below mean anything.
                UssPalette.EnsureInstalled(group);

                List<RadioButton> buttons = new List<RadioButton>();
                group.Query<RadioButton>().ForEach(buttons.Add);
                int count = Math.Min(buttons.Count, loadouts.Count);
                for (int i = 0; i < count; i++)
                {
                    EPlayerRole role = PlayerDataHelper.GetPlayerRole(loadouts[i].GetData());
                    foreach (ClassRecord rec in ClassRegistry.Classes)
                        buttons[i].EnableInClassList(UssPalette.RoleClass(rec),
                                                     (int)role - 1 == rec.Ordinal);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: loadout slot class toggle failed: " + e);
            }
        }
    }

    /// <summary>
    /// PerkTreeVE.CreateClassTooltip (run from DelayedSetup, ~1s after attach) re-localizes
    /// the ClassName label: text -> GetLocalized(text + "_h"). A framework class has no
    /// localization entry, so the title blanks. Restore the manifest name after the game's
    /// pass, matching our trees by element name (the ordinal string).
    /// </summary>
    [HarmonyPatch(typeof(PerkTreeVE), "CreateClassTooltip")]
    internal static class Patch_PerkTreeVE_CreateClassTooltip
    {
        [HarmonyPostfix]
        private static void Postfix(PerkTreeVE __instance)
        {
            try
            {
                int ordinal;
                if (!int.TryParse(__instance.name, out ordinal)) return;
                foreach (ClassRecord rec in ClassRegistry.Classes)
                {
                    if (rec.Ordinal != ordinal) continue;
                    Label label = __instance.Q<Label>("ClassName");
                    if (label != null)
                        label.text = rec.Manifest.className.ToUpperInvariant();
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: class-name restore failed: " + e);
            }
        }
    }
}
