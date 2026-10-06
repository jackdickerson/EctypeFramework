using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

using UI;
using UI.Token;

namespace Ectype
{
    /// <summary>
    /// The observation half of the tree presentation: which document a controller is
    /// driving, whether the player has excluded it, and what geometry it actually resolved
    /// to. Nothing here writes a style property or changes a game element - every member
    /// queries and reports, so a change on this side cannot alter what the screen does.
    ///
    /// One controller serves two differently-authored documents (token terminal and codex),
    /// which is why a document needs an identity at all: it is the key the geometry log is
    /// filed under and the string ScrollExcludeDocuments matches against.
    /// </summary>
    internal static class PresentationDiagnostics
    {
        // -------------------------------------------------------------- document identity

        /// <summary>"UIDocument-name/GameObject-name/visualTreeAsset-name" - the strings
        /// ScrollExcludeDocuments matches against, and the geometry log's document key.</summary>
        internal static string DescribeDocument(VisualElement root)
        {
            try
            {
                foreach (UIDocument doc in UnityEngine.Object.FindObjectsOfType<UIDocument>())
                {
                    VisualElement docRoot = doc.rootVisualElement;
                    if (docRoot == null || (docRoot != root && !docRoot.Contains(root)))
                        continue;
                    return doc.name + "/" + doc.gameObject.name + "/" +
                           (doc.visualTreeAsset != null ? doc.visualTreeAsset.name : "?");
                }
            }
            catch { }
            return "unknown-document";
        }

        internal static bool IsExcluded(string docId)
        {
            string raw = EctypeConfig.ScrollExcludeDocuments != null
                ? EctypeConfig.ScrollExcludeDocuments.Value : null;
            if (string.IsNullOrEmpty(raw)) return false;
            foreach (string part in raw.Split(','))
            {
                string p = part.Trim();
                if (p.Length > 0 &&
                    docId.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ geometry log

        /// <summary>How often the geometry watcher samples, and how many samples pass
        /// before an unresolved layout is classified.</summary>
        private const int GEOM_POLL_MS = 500;
        private const int GEOM_VERDICT_MS = 20000;

        /// <summary>
        /// One live watcher per watched element, weak-keyed so a rebuilt document gets a
        /// fresh watcher and nothing here keeps a dead element alive.
        /// </summary>
        private sealed class GeometryWatch
        {
            public string Shape;
            public IVisualElementScheduledItem Item;
        }

        private static readonly ConditionalWeakTable<VisualElement, GeometryWatch>
            _geometryWatches = new ConditionalWeakTable<VisualElement, GeometryWatch>();

        /// <summary>
        /// A layout self-check, scheduled only while [Debug] VerboseLogging is on. An unshown
        /// document has no resolved layout, which is expected rather than a fault, so it is
        /// not reported and the watcher keeps running until the document really does lay out.
        ///
        /// Resolution is detected by POLLING the row's width, not by GeometryChangedEvent,
        /// which fires only on a layout *change* and so never arrives for a controller
        /// rebuilt into an already-sized document.
        ///
        /// The only line it emits is for a document attached, unhidden, in a sized panel and
        /// still without a width after 20 s: a real layout failure, warned once with the
        /// geometry dump appended.
        /// </summary>
        internal static void ScheduleGeometryLog(VisualElement root, string docId,
                                                 int liveTreeCount, bool wrapped)
        {
            VisualElement watch =
                (VisualElement)root.Q<ScrollView>(Presentation.SCROLL_NAME) ?? root;
            string shape = docId + "|" + liveTreeCount + "|" + (wrapped ? "on" : "off");

            GeometryWatch existing;
            if (_geometryWatches.TryGetValue(watch, out existing))
            {
                // Same document, same build shape: the first watcher already covers it.
                if (existing.Shape == shape) return;
                // A different shape (tree count or wrapping changed under host suppression)
                // is worth re-logging, but only from one watcher.
                if (existing.Item != null) existing.Item.Pause();
                _geometryWatches.Remove(watch);
            }

            GeometryWatch state = new GeometryWatch();
            state.Shape = shape;
            _geometryWatches.Add(watch, state);

            int[] ticks = { 0 };
            state.Item = watch.schedule.Execute((Action)delegate
            {
                try
                {
                    ticks[0]++;
                    VisualElement row = FindGeometryRow(root);
                    VisualElement measured = row != null ? row : watch;
                    float w = measured.layout.width;
                    if (!float.IsNaN(w) && w >= 1f)
                    {
                        if (state.Item != null) state.Item.Pause();
                        return;
                    }

                    if (ticks[0] * GEOM_POLL_MS < GEOM_VERDICT_MS) return;

                    // Not laid out because it is unshown: expected, keep polling until it
                    // really does lay out.
                    if (DescribeUnshown(measured) != null) return;

                    if (state.Item != null) state.Item.Pause();
                    Plugin.Log.LogWarning("Ectype geometry [" + docId + "]: document is " +
                                          "attached, shown and in a sized panel, but its tree " +
                                          "row still has no width after " +
                                          (GEOM_VERDICT_MS / 1000) + "s - layout failed. " +
                                          BuildGeometryLog(root, docId, liveTreeCount, wrapped));
                }
                catch (Exception e)
                {
                    if (state.Item != null) state.Item.Pause();
                    Plugin.Log.LogError("Ectype: geometry watcher failed: " + e);
                }
            }).Every(GEOM_POLL_MS);
        }

        /// <summary>The element the dump's numbers come from and the resolution test
        /// measures: the scroller's content container when wrapped, otherwise the authored
        /// row the trees sit in. Null when the document holds no PerkTreeVE at all.</summary>
        private static VisualElement FindGeometryRow(VisualElement root)
        {
            ScrollView scroll = root.Q<ScrollView>(Presentation.SCROLL_NAME);
            if (scroll != null) return scroll.contentContainer;
            PerkTreeVE anyTree = root.Q<PerkTreeVE>();
            return anyTree != null ? anyTree.parent : null;
        }

        /// <summary>
        /// Why this element cannot have a resolved layout - detached from any panel, under a
        /// display:none ancestor, or in a panel whose root has no size - or null when nothing
        /// observable explains it, which is the only case that deserves a warning.
        /// </summary>
        private static string DescribeUnshown(VisualElement el)
        {
            if (el == null) return "this document holds no tree row";
            if (el.panel == null) return "it is not attached to a panel";

            VisualElement top = el;
            for (VisualElement v = el; v != null; v = v.parent)
            {
                top = v;
                if (v.resolvedStyle.display == DisplayStyle.None)
                    return "ancestor " + Name(v) + " is display:none";
            }

            float w = top.layout.width;
            if (float.IsNaN(w) || w < 1f)
                return "its panel root " + Name(top) + " has no size (never rendered)";
            return null;
        }

        private static string Name(VisualElement v)
        {
            return "'" + (string.IsNullOrEmpty(v.name) ? v.GetType().Name : v.name) + "'";
        }

        /// <summary>
        /// Document identity, scroller state, viewport and row geometry, the full ancestor
        /// chain up to the panel root (with display called out, because that is what tells a
        /// hidden tab apart from an unsized panel), and every tree's box.
        /// </summary>
        private static string BuildGeometryLog(VisualElement root, string docId,
                                               int liveTreeCount, bool wrapped)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("Ectype geometry [").Append(docId).Append("] trees=")
                  .Append(liveTreeCount).Append(" scroller=")
                  .Append(wrapped ? "on" : "off");

                ScrollView scroll = root.Q<ScrollView>(Presentation.SCROLL_NAME);
                VisualElement row = FindGeometryRow(root);
                if (scroll != null && row != null)
                {
                    sb.Append(" | viewport=")
                      .Append(scroll.contentViewport.layout.width.ToString("0"))
                      .Append(" content=").Append(row.layout.width.ToString("0"))
                      .Append(" scrollable=")
                      .Append(Mathf.Max(0f, row.layout.width -
                              scroll.contentViewport.layout.width).ToString("0"))
                      .Append(" offset=").Append(scroll.scrollOffset.x.ToString("0"));
                }

                if (row != null)
                {
                    sb.Append(" | row '").Append(row.name).Append("' w=")
                      .Append(row.layout.width.ToString("0"))
                      .Append(" h=").Append(row.layout.height.ToString("0"))
                      .Append(" dir=").Append(row.resolvedStyle.flexDirection);
                    VisualElement up = row.parent;
                    for (int i = 0; up != null && i < 12; i++, up = up.parent)
                    {
                        sb.Append(" < ").Append(Name(up)).Append(" w=")
                          .Append(up.layout.width.ToString("0"));
                        if (up.resolvedStyle.display == DisplayStyle.None)
                            sb.Append(" display=None");
                    }

                    foreach (VisualElement child in row.Children())
                    {
                        PerkTreeVE t = child as PerkTreeVE;
                        if (t == null) continue;
                        sb.Append(" | tree '").Append(t.name).Append("' x=")
                          .Append(t.layout.x.ToString("0"))
                          .Append(" y=").Append(t.layout.y.ToString("0"))
                          .Append(" w=").Append(t.layout.width.ToString("0"))
                          .Append(" h=").Append(t.layout.height.ToString("0"))
                          .Append(" grow=")
                          .Append(t.resolvedStyle.flexGrow.ToString("0.#"))
                          .Append(" shrink=")
                          .Append(t.resolvedStyle.flexShrink.ToString("0.#"));
                    }
                }
                else sb.Append(" | no PerkTreeVE found");

                return sb.ToString();
            }
            catch (Exception e)
            {
                return "Ectype geometry [" + docId + "]: dump failed: " + e;
            }
        }
    }
}
