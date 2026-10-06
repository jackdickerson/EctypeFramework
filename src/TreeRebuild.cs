using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

using ResourceAssets;
using UI.Codex;
using UI.MainHUD;
using UI.Token;

namespace Ectype
{
    /// <summary>
    /// Rebuilds the gene-tree screens that are already built, so a suppression change shows
    /// without a respawn. Each loadout terminal and the codex construct their
    /// PerkTreeController once, in their own Start, and nothing in the game rebuilds it.
    ///
    /// A rebuild is dispose, fresh elements, construct: the same construction the owner's
    /// Start ran, so the ctor prefix injects and removes trees exactly as on the first build.
    /// The elements come from the authored template because a disposed tree still carries its
    /// runtime state, and a suppressed vanilla tree is no longer in the document at all. A
    /// screen whose Start has not reached its controller yet is skipped; it builds from the
    /// current state when it gets there.
    /// </summary>
    internal static class TreeRebuild
    {
        private static readonly FieldInfo TerminalControllerF =
            AccessTools.Field(typeof(TokenTerminal), "treeController");
        private static readonly FieldInfo TerminalDocF =
            AccessTools.Field(typeof(TokenTerminal), "doc");
        private static readonly FieldInfo TerminalTreesF =
            AccessTools.Field(typeof(TokenTerminal), "perkTrees");
        private static readonly FieldInfo CodexControllerF =
            AccessTools.Field(typeof(LogBookManager), "treeController");
        private static readonly FieldInfo CodexDocF =
            AccessTools.Field(typeof(LogBookManager), "doc");

        public static void RebuildAll()
        {
            int rebuilt = 0;
            // FindObjectsOfTypeAll: a terminal on a currently inactive GameObject still holds a
            // built controller, and would show the old trees when it activates.
            foreach (TokenTerminal term in Resources.FindObjectsOfTypeAll<TokenTerminal>())
            {
                if (!term.gameObject.scene.IsValid()) continue;   // prefab asset
                TokenTerminal t = term;
                UIDocument doc = Rebuild(t, TerminalControllerF, TerminalDocF, false, root =>
                    new PerkTreeController(PerkTreeContainer.Instance.AssetDescriptions, root,
                                           t.Events, t.GetComponent<ILocalizationProvider>(),
                                           t.InteractionInfo, t.LookupOnly));
                if (doc == null) continue;
                rebuilt++;
                try
                {
                    if (TerminalTreesF != null)
                        TerminalTreesF.SetValue(t, doc.rootVisualElement.Query<PerkTreeVE>().ToList());
                    t.UpdateLeftSide();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: refreshing a terminal after its tree rebuild " +
                                        "failed: " + e);
                }
            }

            foreach (LogBookManager codex in Resources.FindObjectsOfTypeAll<LogBookManager>())
            {
                if (!codex.gameObject.scene.IsValid()) continue;
                LogBookManager c = codex;
                if (Rebuild(c, CodexControllerF, CodexDocF, true, root =>
                        new PerkTreeController(PerkTreeContainer.Instance.AssetDescriptions, root,
                                               c.Events, c.GetComponent<ILocalizationProvider>(),
                                               null, true)) != null)
                    rebuilt++;
            }

            if (rebuilt > 0)
                Plugin.Log.LogInfo("Ectype: rebuilt " + rebuilt + " gene-tree screen(s) for the " +
                                   "suppression change.");
        }

        /// <summary>Returns the rebuilt document, or null when the screen was left as built.</summary>
        private static UIDocument Rebuild(MonoBehaviour owner, FieldInfo controllerF, FieldInfo docF,
                                          bool inCodex,
                                          Func<VisualElement, PerkTreeController> construct)
        {
            if (controllerF == null || docF == null) return null;
            try
            {
                PerkTreeController old = controllerF.GetValue(owner) as PerkTreeController;
                UIDocument doc = docF.GetValue(owner) as UIDocument;
                if (old == null || doc == null || doc.rootVisualElement == null) return null;
                VisualElement root = doc.rootVisualElement;

                List<PerkTreeVE> live = root.Query<PerkTreeVE>().ToList();
                List<PerkTreeVE> fresh = PristineTrees(doc);
                if (live.Count == 0 || live[0].parent == null || fresh == null || fresh.Count == 0)
                {
                    Plugin.Log.LogWarning("Ectype: gene-tree screen on '" + owner.name + "' has " +
                                          "no trees or no authored template - left as built, " +
                                          "so it shows the old suppression until a respawn.");
                    return null;
                }

                old.Dispose();
                // Never leave the owner holding a disposed controller if construction throws.
                controllerF.SetValue(owner, null);

                VisualElement row = live[0].parent;
                int at = row.IndexOf(live[0]);
                foreach (PerkTreeVE tree in live) tree.RemoveFromHierarchy();
                foreach (PerkTreeVE tree in fresh)
                {
                    // The codex flags its trees before construction; a fresh element is unflagged.
                    if (inCodex) tree.InCodex = true;
                    row.Insert(at++, tree);
                }

                controllerF.SetValue(owner, construct(root));
                return doc;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: gene-tree rebuild on '" + owner.name + "' failed: " + e);
                return null;
            }
        }

        /// <summary>Every authored tree, detached from a fresh instantiation of the document's
        /// own asset, in authored order.</summary>
        private static List<PerkTreeVE> PristineTrees(UIDocument doc)
        {
            if (doc.visualTreeAsset == null) return null;
            List<PerkTreeVE> trees = doc.visualTreeAsset.Instantiate().Query<PerkTreeVE>().ToList();
            foreach (PerkTreeVE tree in trees) tree.RemoveFromHierarchy();
            return trees;
        }
    }
}
