using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Every tunable number the gene-tree presentation uses, in one object that can be
    /// re-read from disk while the game runs.
    ///
    /// The defaults below are the shipping values; the file only ever overrides them, and a
    /// missing or malformed one leaves them intact. When a tuning session settles, transcribe
    /// the accepted numbers back into these defaults and delete the file - it is a workbench,
    /// never a shipped artifact.
    /// </summary>
    public class LayoutSpec
    {
        /// <summary>Trees per screen. Every tree is sized to viewport/this, so raising it
        /// shows more, smaller trees and re-scales each tree's internals proportionally;
        /// extras beyond one screen scroll.</summary>
        public int treesPerPage = 4;

        /// <summary>
        /// Uniform visual scale on each tree's CONTENT - the hero button and the perk grid -
        /// about the tree's top-centre. The frame sprite is a sibling of the content and
        /// deliberately excluded, so the freed space appears INSIDE the frame rather than
        /// shrinking it. Applied as a transform, so nothing reflows or clips.
        /// </summary>
        public float treeScale = 0.958f;

        /// <summary>
        /// Pixels reserved at the top of the frame, inside it, for the role name; applied as
        /// a downward translate on the content, spending the space treeScale freed.
        ///
        /// This and treeScale are one setting in two halves. Keep
        /// <c>treeScale = 1 - headerBandPx/contentHeight</c> (contentHeight being the authored
        /// distance from the tree's top to the bottom of its grid), or the grid pushes out
        /// through the bottom of the frame; the geometry log prints the numbers to check
        /// against.
        /// </summary>
        public float headerBandPx = 34f;

        /// <summary>
        /// Role-name label overrides. The label is the game's own, so every field here is
        /// null-means-leave-alone: an omitted key keeps the authored behaviour exactly, which
        /// is what makes a bad tuning session recoverable by deleting the file.
        /// </summary>
        public bool? showRoleName = null;
        public float? roleNameFontSize = null;
        public string roleNameColorHex = null;
        /// <summary>Absolute placement of the name's container, measured from the top of the
        /// tree's own box; the authored position is a NEGATIVE top that puts the name outside
        /// the frame, so these are what bring it inside. Set both and keep top + height inside
        /// headerBandPx, or leave both null for the authored position.</summary>
        public float? roleNameTopPx = 4f;
        public float? roleNameHeightPx = 26f;

        // ------------------------------------------------------------------ the scroller

        /// <summary>Seconds for one eased scroll step.</summary>
        public float scrollDurationSec = 0.42f;

        /// <summary>Cubic-bezier control points for that easing, CSS timing-function
        /// convention. The shipped pair is a firm entry with the settle softened toward the
        /// ease-in-out family; (0.35, 0, 0, 1) brakes hard enough to feel mechanical.</summary>
        public float easeP1x = 0.35f, easeP1y = 0f, easeP2x = 0.15f, easeP2y = 1f;

        /// <summary>One wheel notch, in viewports. 0.25 with four trees per page is one
        /// tree.</summary>
        public float wheelStepPages = 0.25f;

        /// <summary>The drawer pass: a tree leaving the viewport slides ahead of the scroll by
        /// up to this many pixels and collapses horizontally to this fraction, both scaled by
        /// how much of it is hidden. Zero travel and a fraction of 1 turn it off.</summary>
        public float drawerSlidePx = 140f;
        public float drawerMinScaleX = 0.88f;

        // -------------------------------------------------------------------- the pager

        /// <summary>
        /// The position indicator: one pip per role, above the row, lit for the roles
        /// currently on screen - a small fixed set answers "which roles am I looking at"
        /// better than a scrollbar's proportion. Off leaves no indicator; the wheel and the
        /// pad still scroll.
        /// </summary>
        public bool showPager = true;

        /// <summary>
        /// The two sprites each pip is built from, by name, looked up among the sprites the
        /// process has already loaded - so this costs no asset loading and ships no art.
        /// The metem pair is authored small (46x42) and reads cleanly at pip size, where the
        /// perk-button set (~473x462) reaches a pip through a heavy downscale; swapping to
        /// <c>OctagonalSkillBtn_background</c> / <c>OctagonalSkillBtn_border</c> is a file
        /// edit.
        /// </summary>
        public string pagerFillSprite = "metem-fill-small";
        public string pagerOutlineSprite = "metem-outline-small";

        public float pagerPipWidthPx = 24f;
        public float pagerPipHeightPx = 22f;
        public float pagerPipGapPx = 8f;

        /// <summary>
        /// Distance from the top of the tree row to the pips, and from the pips down to the
        /// trees. The pager is in flow, so these are plain margins and the trees move down by
        /// exactly their sum.
        ///
        /// The top margin is NEGATIVE on purpose: the row carries roughly fifty pixels of
        /// authored space above it and the pager belongs inside that band, not below it.
        /// Moving the pips without moving the trees means changing both numbers by the same
        /// amount in opposite directions.
        /// </summary>
        public float pagerTopPx = -24f;
        public float pagerGapPx = 36f;

        /// <summary>
        /// Trailing margin that slides the pager off the row's centre and onto the seam
        /// between the two middle trees of the opening view. It is
        /// <c>marginRight - marginLeft</c> of a tree (8, against the authored 22 and 30):
        /// with trees at <c>x_i = ml + i*pitch</c>, the seam minus the block centre works out
        /// to <c>(ml - mr)/2</c>, so tree width, pitch, trees-per-page and viewport width all
        /// cancel and one constant is correct at every screen size. Only the game re-authoring
        /// the tree margins invalidates it.
        /// </summary>
        public float pagerSeamNudgePx = 8f;

        /// <summary>
        /// How much of a tree must be on screen for its pip to light, as a fraction of the
        /// tree's width - so it means the same thing at every viewport size. Half makes a tree
        /// straddling the viewport edge commit to one side rather than lighting both pips.
        /// </summary>
        public float pagerLitFraction = 0.5f;

        /// <summary>Pip tints. The sprites are white silhouettes, so these are the whole of
        /// their appearance: lit is the terminal's gold, unlit a dark fill inside a
        /// medium-dark outline.</summary>
        public string pagerLitFillHex = "#E8B44A";
        public string pagerLitOutlineHex = "#F6DFA6";
        public string pagerUnlitFillHex = "#161A1F";
        public string pagerUnlitOutlineHex = "#5A6068";

        // ----------------------------------------------------------------------- helpers

        public static Color ParseColor(string hex, Color fallback)
        {
            Color c;
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out c))
                return c;
            return fallback;
        }
    }

    /// <summary>
    /// Owns the live <see cref="LayoutSpec"/> and the file it can be reloaded from. Nothing
    /// caches a value out of the spec: every consumer reads <see cref="Spec"/> at the point
    /// of use, which is what makes a reload take effect without rebuilding anything.
    /// </summary>
    internal static class DevLayout
    {
        /// <summary>Never null. A failed load leaves the previous (or default) spec in
        /// place - a half-parsed layout would be worse than the one already on screen.</summary>
        public static LayoutSpec Spec = new LayoutSpec();

        public static string LastStatus = "defaults (no file loaded)";

        /// <summary>Input and both outputs share one directory so a tuning loop - whoever
        /// or whatever is running it - watches a single place.</summary>
        public static string Dir
        {
            get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "EctypeLayout"); }
        }

        public static string FilePath { get { return Path.Combine(Dir, "layout.json"); } }
        public static string CapturePath { get { return Path.Combine(Dir, "capture.png"); } }
        public static string StatusPath { get { return Path.Combine(Dir, "status.json"); } }

        public static void EnsureDir()
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
        }

        /// <summary>Re-read the file. Returns whether the live spec changed; the caller
        /// decides whether that warrants re-applying to open documents.</summary>
        public static bool Reload()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Spec = new LayoutSpec();
                    LastStatus = "no file at " + FilePath + " - using defaults";
                    Plugin.Log.LogInfo("Ectype: " + LastStatus);
                    return true;
                }

                string json = File.ReadAllText(FilePath);
                LayoutSpec loaded = JsonConvert.DeserializeObject<LayoutSpec>(json);
                if (loaded == null)
                {
                    LastStatus = "file parsed to nothing - keeping the current layout";
                    Plugin.Log.LogWarning("Ectype: " + LastStatus);
                    return false;
                }
                Spec = loaded;
                LastStatus = "loaded " + Path.GetFileName(FilePath) + " (treeScale=" +
                             loaded.treeScale.ToString("0.###") + " band=" +
                             loaded.headerBandPx.ToString("0") + " perPage=" +
                             loaded.treesPerPage + ")";
                Plugin.Log.LogInfo("Ectype: layout " + LastStatus);
                return true;
            }
            catch (Exception e)
            {
                LastStatus = "load failed, keeping the current layout: " + e.Message;
                Plugin.Log.LogError("Ectype: layout " + LastStatus + " (" + e + ")");
                return false;
            }
        }

        /// <summary>Write the live spec out, so the file starts as a complete, valid
        /// document with every key at its current value rather than something hand-typed
        /// from the source.</summary>
        public static void WriteTemplate()
        {
            try
            {
                EnsureDir();
                File.WriteAllText(FilePath,
                    JsonConvert.SerializeObject(Spec, Formatting.Indented));
                LastStatus = "wrote the current layout to " + FilePath;
                Plugin.Log.LogInfo("Ectype: " + LastStatus);
            }
            catch (Exception e)
            {
                LastStatus = "template write failed: " + e.Message;
                Plugin.Log.LogError("Ectype: layout " + LastStatus);
            }
        }

        // ------------------------------------------------------------------- the watcher

        /// <summary>The timestamp of the layout file as of the last successful load. What
        /// the acknowledgement in status.json reports, so a watcher can tell whether the
        /// game has taken up the edit it just made rather than timing a guess.</summary>
        public static long LoadedFileTicks;

        private static long _seenTicks;
        private static float _nextPoll;

        /// <summary>Record the layout file's current write time as loaded, and stop the
        /// watcher re-firing on the same edit. Called by the manual reload paths so a
        /// keypress and a watcher tick leave identical state behind.</summary>
        public static void MarkLoadedFileTime()
        {
            try
            {
                if (!File.Exists(FilePath)) { LoadedFileTicks = 0; return; }
                LoadedFileTicks = File.GetLastWriteTimeUtc(FilePath).Ticks;
                _seenTicks = LoadedFileTicks;
            }
            catch { }
        }

        /// <summary>
        /// Poll the layout file's write time and, when it moves, run the whole cycle:
        /// reload, re-style the open terminal, capture the panel, write status. The check is
        /// a single timestamp read, so a short interval costs effectively nothing.
        ///
        /// This is what makes the loop hands-free: the keypress and F5 reload paths both
        /// need the game focused, and the editor is a different window.
        /// </summary>
        public static void Poll()
        {
            if (EctypeConfig.LayoutPollSeconds == null) return;
            float interval = EctypeConfig.LayoutPollSeconds.Value;
            if (interval <= 0f) return;                     // 0 = watcher off
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + interval;

            long ticks;
            try
            {
                if (!File.Exists(FilePath)) return;
                ticks = File.GetLastWriteTimeUtc(FilePath).Ticks;
            }
            catch { return; }

            if (ticks == _seenTicks) return;

            // Advance the seen-timestamp only on a successful load, so a torn read of a
            // non-atomic write is retried next tick instead of swallowed as "already handled".
            if (!Reload()) return;
            _seenTicks = ticks;
            LoadedFileTicks = ticks;

            DevInspector.ApplyAndCapture("watcher");
        }
    }
}
