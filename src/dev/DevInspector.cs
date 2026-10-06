using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

using UI.Token;

namespace Ectype
{
    /// <summary>
    /// A visual-tree dumper for the gene-tree screen, and the handles that let a layout
    /// change be re-applied to a document that is already on screen. Stands in for the UI
    /// Toolkit Debugger, which ships only in the editor.
    /// </summary>
    internal static class DevInspector
    {
        public static string LastSummary = "<not run>";

        /// <summary>
        /// Everything a watcher outside the process needs to know about one reload, written
        /// as the last step of it. <c>sourceFileTicks</c> echoes the write time of the layout
        /// file this cycle actually loaded, so a watcher can wait for its own edit to be
        /// named instead of sleeping an interval and racing a stale capture.
        /// </summary>
        private class CaptureStatus
        {
            public int sequence;
            public string trigger;
            public string utc;
            public long sourceFileTicks;
            public string layoutStatus;
            public bool terminalOpen;
            public bool captureWritten;
            public int captureWidth, captureHeight;
            public string note;
        }

        private static int _sequence;

        /// <summary>
        /// One cycle: push the current spec into the open terminal, capture the panel, and
        /// record what happened. Every path writes a status file, including those that
        /// captured nothing - a PNG's timestamp cannot distinguish "no terminal was open"
        /// from "the capture failed" from "it worked".
        /// </summary>
        public static void ApplyAndCapture(string trigger)
        {
            CaptureStatus st = new CaptureStatus();
            st.sequence = ++_sequence;
            st.trigger = trigger;
            st.utc = DateTime.UtcNow.ToString("o");
            st.sourceFileTicks = DevLayout.LoadedFileTicks;
            st.layoutStatus = DevLayout.LastStatus;

            VisualElement root = null;
            try
            {
                root = ActiveRoot();
                st.terminalOpen = root != null;
                if (root == null)
                {
                    st.note = "no terminal open; the layout is loaded and lands when one " +
                              "is next built.";
                }
                else
                {
                    Presentation.Restyle(root);
                    st.note = "re-styled the open terminal.";
                }
            }
            catch (Exception e)
            {
                st.note = "re-apply threw: " + e.Message;
                Plugin.Log.LogError("Ectype: layout re-apply threw: " + e);
            }

            if (root == null) { Finish(st); return; }

            // The capture MUST NOT happen in this frame: re-styling only marks the panel
            // dirty, so the render texture still holds the pre-change image and capturing
            // now returns the previous layout, which reads as "my edit did nothing".
            // Scheduling on the document also means a pending capture dies with the panel.
            root.schedule.Execute((Action)delegate
            {
                try
                {
                    int w, h;
                    if (CapturePanel(out w, out h))
                    {
                        st.captureWritten = true;
                        st.captureWidth = w;
                        st.captureHeight = h;
                    }
                }
                catch (Exception e)
                {
                    st.note += " capture threw: " + e.Message;
                    Plugin.Log.LogError("Ectype: panel capture threw: " + e);
                }
                Finish(st);
            }).StartingIn(REPAINT_DELAY_MS);
        }

        /// <summary>Long enough for a layout pass, a repaint and a render at any frame rate
        /// this loop is used at, short enough not to be felt. The status file is written
        /// after the capture, never before, so a watcher that waits for the status can read
        /// the PNG immediately.</summary>
        private const long REPAINT_DELAY_MS = 250;

        private static void Finish(CaptureStatus st)
        {
            WriteStatus(st);
            LastSummary = DevLayout.LastStatus + "; " + st.note +
                          (st.captureWritten
                              ? " Captured " + st.captureWidth + "x" + st.captureHeight + "."
                              : " No capture.");
            Plugin.Log.LogInfo("Ectype: layout cycle #" + st.sequence + " (" + st.trigger +
                               ") - " + LastSummary);
        }

        /// <summary>
        /// Write the terminal's own render texture out as a PNG - the panel exactly as
        /// authored, square-on at its own resolution, independent of the camera and of
        /// whether the game window is focused or even visible.
        /// </summary>
        private static bool CapturePanel(out int width, out int height)
        {
            width = height = 0;
            object term = ActiveTerminal();
            if (term == null) return false;

            FieldInfo f = term.GetType().GetField("_renderTexture",
                              BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance);
            RenderTexture rt = f != null ? f.GetValue(term) as RenderTexture : null;
            if (rt == null)
            {
                Plugin.Log.LogWarning("Ectype: the open terminal has no render texture in " +
                                      "this game version - cannot capture the panel.");
                return false;
            }

            RenderTexture prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
                tex.Apply();
                DevLayout.EnsureDir();
                System.IO.File.WriteAllBytes(DevLayout.CapturePath, tex.EncodeToPNG());
                width = rt.width;
                height = rt.height;
                return true;
            }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        private static void WriteStatus(CaptureStatus st)
        {
            try
            {
                DevLayout.EnsureDir();
                System.IO.File.WriteAllText(DevLayout.StatusPath,
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        st, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: status write failed: " + e);
            }
        }

        /// <summary>The live world-space terminal, or null when no terminal is open. The
        /// field is static on the terminal component; read reflectively so a game-side
        /// change to it degrades to "no terminal" instead of a missing-member crash.</summary>
        private static object ActiveTerminal()
        {
            try
            {
                Type t = AccessTools.TypeByName("CG.Client.UI.Terminals.WorldSpaceUI");
                if (t == null) return null;
                FieldInfo f = t.GetField("ActiveWorldSpaceUI",
                                         BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static);
                return f != null ? f.GetValue(null) : null;
            }
            catch { return null; }
        }

        /// <summary>The open terminal's root element - the tree the gene-tree screen was
        /// built into, and what a re-apply pass operates on.</summary>
        public static VisualElement ActiveRoot()
        {
            try
            {
                object term = ActiveTerminal();
                if (term == null) return null;
                FieldInfo f = term.GetType().GetField("rootElement",
                                  BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Instance);
                return f != null ? f.GetValue(term) as VisualElement : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Re-read the layout file and push it into the already-open terminal without a
        /// rebuild: the presentation pass is a pure function of (document, spec), so
        /// re-running it against a live document is the whole reload, and scroll position
        /// and selection survive.
        /// </summary>
        public static void ReloadAndApply()
        {
            if (DevLayout.Reload()) DevLayout.MarkLoadedFileTime();
            ApplyAndCapture("manual");
        }

        /// <summary>
        /// Rebuild the open terminal's panel from its VisualTreeAsset. Heavier than a
        /// re-style and it discards scroll position and focus, but it re-runs the game's own
        /// controller construction - so it is the only way to see a change that happens
        /// during injection rather than during styling.
        /// </summary>
        public static void RebuildTerminal()
        {
            try
            {
                object term = ActiveTerminal();
                if (term == null)
                {
                    LastSummary = "no terminal open to rebuild.";
                    return;
                }
                MethodInfo m = term.GetType().GetMethod("RebuildPanel",
                                   BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null)
                {
                    LastSummary = "the terminal has no RebuildPanel() in this game version.";
                    Plugin.Log.LogWarning("Ectype: " + LastSummary);
                    return;
                }
                if (DevLayout.Reload()) DevLayout.MarkLoadedFileTime();
                m.Invoke(term, null);
                LastSummary = DevLayout.LastStatus + "; terminal panel rebuilt.";
                Plugin.Log.LogInfo("Ectype: " + LastSummary);
            }
            catch (Exception e)
            {
                LastSummary = "rebuild threw: " + e.Message;
                Plugin.Log.LogError("Ectype: terminal rebuild threw: " + e);
            }
        }

        // ---------------------------------------------------------------------- the dump

        /// <summary>
        /// Dump the first gene tree in the open terminal, element by element. One tree is
        /// enough - they are instantiations of the same authored subtree - and a whole
        /// terminal root runs to thousands of lines.
        /// </summary>
        public static void DumpTree()
        {
            try
            {
                VisualElement root = ActiveRoot();
                if (root == null)
                {
                    LastSummary = "no terminal open - stand at the loadout terminal first.";
                    Plugin.Log.LogWarning("Ectype: " + LastSummary);
                    return;
                }

                PerkTreeVE tree = root.Q<PerkTreeVE>();
                if (tree == null)
                {
                    LastSummary = "no PerkTreeVE in the open terminal.";
                    Plugin.Log.LogWarning("Ectype: " + LastSummary);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("Ectype visual tree, from PerkTreeVE '").Append(tree.name)
                  .AppendLine("' (ancestors first):");
                // The ancestor chain matters as much as the subtree: an element that renders
                // nowhere is very often fine in itself and clipped by something above it.
                VisualElement up = tree.parent;
                int depth = 0;
                while (up != null && depth < 6)
                {
                    sb.Append("  ^ ");
                    Describe(sb, up);
                    sb.AppendLine();
                    up = up.parent;
                    depth++;
                }
                sb.AppendLine("  --- subtree ---");
                Walk(sb, tree, 0);

                Plugin.Log.LogInfo(sb.ToString());
                // The file is the copy that survives to be diffed against the next dump; the
                // log interleaves it with everything else and rotates it away.
                string path = WriteDumpFile("tree-dump.txt", sb.ToString());
                LastSummary = "dumped tree '" + tree.name + "' to LogOutput.log" +
                              (path != null ? " and " + path : "") + ".";
            }
            catch (Exception e)
            {
                LastSummary = "dump threw: " + e.Message;
                Plugin.Log.LogError("Ectype: visual tree dump threw: " + e);
            }
        }

        // ------------------------------------------------------------------ chrome recon

        /// <summary>
        /// What the game already ships for a piece of UI chrome, on two axes: the loaded
        /// sprites whose names match, and the USS rules that mention them. Sprites but no
        /// rules means the styling is ours to write; rules as well means the cheaper route is
        /// to borrow the game's classes; neither means the search term is wrong.
        /// </summary>
        private static readonly string[] RECON_TERMS =
            { "scroller", "scrollbar", "slider", "dragger", "perktree", "skilltree",
              "arrow", "pager", "chevron" };

        public static void DumpChrome()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("Ectype chrome recon; terms:");
                foreach (string t in RECON_TERMS) sb.Append(' ').Append(t);
                sb.AppendLine();

                sb.AppendLine();
                sb.AppendLine("=== loaded sprites and textures ===");
                int sprites = 0;
                foreach (Sprite sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sp != null && Matches(sp.name))
                    {
                        Rect r = sp.rect;
                        sb.Append("  Sprite  \"").Append(sp.name).Append("\"  ")
                          .Append(r.width.ToString("0")).Append("x")
                          .Append(r.height.ToString("0"))
                          .Append("  border=").Append(sp.border)
                          .AppendLine();
                        sprites++;
                    }
                foreach (Texture2D tx in Resources.FindObjectsOfTypeAll<Texture2D>())
                    if (tx != null && Matches(tx.name))
                    {
                        sb.Append("  Texture \"").Append(tx.name).Append("\"  ")
                          .Append(tx.width).Append("x").Append(tx.height).AppendLine();
                        sprites++;
                    }
                if (sprites == 0) sb.AppendLine("  (none - no matching art is loaded)");

                sb.AppendLine();
                sb.AppendLine("=== USS selectors mentioning those terms ===");
                int rules = 0;
                foreach (StyleSheet sheet in Resources.FindObjectsOfTypeAll<StyleSheet>())
                {
                    if (sheet == null) continue;
                    bool named = false;
                    // No public API reports what a rule contains; a StyleSheet is a
                    // ScriptableObject of plain serialized arrays. UssDecoder does that
                    // reading - this half only decides which rules are worth printing.
                    IEnumerable selectors =
                        Traverse.Create(sheet).Property("complexSelectors").GetValue()
                        as IEnumerable;
                    if (selectors == null) continue;
                    foreach (object cs in selectors)
                    {
                        string text = UssDecoder.SelectorText(cs);
                        if (text == null || !Matches(text)) continue;
                        if (!named)
                        {
                            sb.Append("  sheet \"").Append(sheet.name).AppendLine("\"");
                            named = true;
                        }
                        sb.Append("      ").AppendLine(text);
                        UssDecoder.AppendDeclarations(sb, sheet, cs);
                        rules++;
                    }
                }
                if (rules == 0)
                    sb.AppendLine("  (none - the game styles no such chrome; ours to draw)");

                sb.AppendLine();
                sb.AppendLine("=== matching elements in the open terminal ===");
                sb.AppendLine("(who OWNS a control decides whether it can be removed: an " +
                              "element under an injected root is ours, anything else is the " +
                              "game's and disappears again on the next panel rebuild)");
                VisualElement root = ActiveRoot();
                if (root == null)
                    sb.AppendLine("  (no terminal open - stand at the loadout terminal)");
                else
                {
                    int hits = FindMatching(sb, root, "");
                    if (hits == 0) sb.AppendLine("  (none)");

                    sb.AppendLine();
                    sb.AppendLine("=== the tree scroller, part by part ===");
                    DumpScroller(sb, root);
                }

                Plugin.Log.LogInfo(sb.ToString());
                string path = WriteDumpFile("chrome-recon.txt", sb.ToString());
                LastSummary = "chrome recon: " + sprites + " art assets, " + rules +
                              " selectors" + (path != null ? " -> " + path : "") + ".";
                Plugin.Log.LogInfo("Ectype: " + LastSummary);
            }
            catch (Exception e)
            {
                LastSummary = "chrome recon threw: " + e.Message;
                Plugin.Log.LogError("Ectype: chrome recon threw: " + e);
            }
        }

        /// <summary>
        /// The scroller as it actually exists, part by part. Only the live subtree can tell
        /// "the selector does not match these parts" from "the selector matches and something
        /// else wins the cascade".
        /// </summary>
        private static void DumpScroller(StringBuilder sb, VisualElement root)
        {
            ScrollView scroll = root.Q<ScrollView>(Presentation.SCROLL_NAME);
            if (scroll == null)
            {
                sb.AppendLine("  (no tree scroller in this document)");
                return;
            }

            sb.Append("  stylesheets on the scroller's ancestors, outermost first:")
              .AppendLine();
            var chain = new System.Collections.Generic.List<VisualElement>();
            for (VisualElement up = scroll; up != null; up = up.parent) chain.Add(up);
            chain.Reverse();
            foreach (VisualElement up in chain)
                for (int i = 0; i < up.styleSheets.count; i++)
                {
                    StyleSheet s = up.styleSheets[i];
                    sb.Append("      ").Append(string.IsNullOrEmpty(up.name)
                                                   ? up.GetType().Name : up.name)
                      .Append("  <-  ").AppendLine(s != null ? s.name : "null");
                }

            Scroller bar = scroll.horizontalScroller;
            if (bar == null)
            {
                sb.AppendLine("  (the scroller has no horizontal scroller element)");
                return;
            }
            sb.AppendLine("  horizontal scroller subtree:");
            Walk(sb, bar, 1);
        }

        /// <summary>Walk the whole panel for elements whose name or classes match, printing
        /// the ancestor path to each. The path is the point: it names the owner, and an
        /// element's own line never says who put it there.</summary>
        private static int FindMatching(StringBuilder sb, VisualElement el, string path)
        {
            int found = 0;
            string here = path + "/" + (string.IsNullOrEmpty(el.name)
                                            ? el.GetType().Name : el.name);
            bool hit = Matches(el.name);
            if (!hit)
                foreach (string c in el.GetClasses())
                    if (Matches(c)) { hit = true; break; }
            if (hit)
            {
                sb.Append("  ").AppendLine(here);
                sb.Append("      ");
                Describe(sb, el);
                sb.AppendLine();
                found++;
            }
            foreach (VisualElement child in el.Children())
                found += FindMatching(sb, child, here);
            return found;
        }

        private static bool Matches(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string term in RECON_TERMS)
                if (s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Dumps share the workbench directory with the layout file and the
        /// capture, so one place holds everything a tuning loop reads or writes. A failed
        /// write is reported and swallowed: the log already has the same text.</summary>
        private static string WriteDumpFile(string fileName, string text)
        {
            try
            {
                DevLayout.EnsureDir();
                string path = System.IO.Path.Combine(DevLayout.Dir, fileName);
                System.IO.File.WriteAllText(path, text);
                return path;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: could not write " + fileName + ": " + e.Message);
                return null;
            }
        }

        private static void Walk(StringBuilder sb, VisualElement el, int indent)
        {
            sb.Append(' ', 2 + indent * 2);
            Describe(sb, el);
            sb.AppendLine();
            // Deep enough for a perk grid's cells and their icon layers, shallow enough that
            // one tree stays readable in a log.
            if (indent >= 8) return;
            foreach (VisualElement child in el.Children())
                Walk(sb, child, indent + 1);
        }

        /// <summary>
        /// One line per element: identity and classes, the resolved box, and the four
        /// independent ways a laid-out element renders as nothing (display, visibility,
        /// opacity, a zero dimension). Resolved values, never inline ones - the question is
        /// what the cascade produced.
        /// </summary>
        private static void Describe(StringBuilder sb, VisualElement el)
        {
            try
            {
                sb.Append(el.GetType().Name);
                if (!string.IsNullOrEmpty(el.name)) sb.Append(" #").Append(el.name);
                foreach (string c in el.GetClasses()) sb.Append(" .").Append(c);

                Rect lay = el.layout;
                sb.Append("  [x=").Append(UssDecoder.Fmt(lay.x))
                  .Append(" y=").Append(UssDecoder.Fmt(lay.y))
                  .Append(" w=").Append(UssDecoder.Fmt(lay.width))
                  .Append(" h=").Append(UssDecoder.Fmt(lay.height))
                  .Append("]");

                IResolvedStyle rs = el.resolvedStyle;
                sb.Append(" display=").Append(rs.display)
                  .Append(" vis=").Append(el.visible)
                  .Append(" opacity=").Append(UssDecoder.Fmt(rs.opacity))
                  .Append(" pick=").Append(el.pickingMode);

                // resolvedStyle exposes no overflow, so clipping is answered geometrically:
                // a box escaping its parent's is what a hidden overflow would cut off.
                if (el.parent != null)
                {
                    Rect p = el.parent.contentRect;
                    if (!float.IsNaN(p.width) && !float.IsNaN(lay.width) &&
                        (lay.x < -0.5f || lay.y < -0.5f ||
                         lay.xMax > p.width + 0.5f || lay.yMax > p.height + 0.5f))
                        sb.Append(" OUTSIDE-PARENT(").Append(UssDecoder.Fmt(p.width))
                          .Append("x").Append(UssDecoder.Fmt(p.height)).Append(")");
                }

                Label label = el as Label;
                if (label != null)
                    sb.Append(" text=\"").Append(label.text).Append("\" font=")
                      .Append(UssDecoder.Fmt(rs.fontSize)).Append(" color=")
                      .Append(ColorUtility.ToHtmlStringRGBA(rs.color));

                if (rs.translate != Vector3.zero)
                    sb.Append(" translate=").Append(rs.translate);
                if (rs.scale.value != Vector3.one)
                    sb.Append(" scale=").Append(rs.scale.value);

                // The background identifies which element actually draws an authored frame,
                // as against merely containing it - nothing else in a dump does.
                Background bg = rs.backgroundImage;
                if (bg.sprite != null)
                    sb.Append(" bgSprite=\"").Append(bg.sprite.name).Append("\"");
                else if (bg.texture != null)
                    sb.Append(" bgTexture=\"").Append(bg.texture.name).Append("\"");
                else if (bg.vectorImage != null)
                    sb.Append(" bgVector=\"").Append(bg.vectorImage.name).Append("\"");
                if (rs.backgroundColor.a > 0.001f)
                    sb.Append(" bgColor=#")
                      .Append(ColorUtility.ToHtmlStringRGBA(rs.backgroundColor));
            }
            catch (Exception e)
            {
                sb.Append(" <describe failed: ").Append(e.Message).Append(">");
            }
        }
    }
}
