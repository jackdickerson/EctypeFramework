using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// The layout workbench: replaces <see cref="LayoutSpec.Current"/> from
    /// BepInEx/EctypeLayout/layout.json while the game runs. The file only ever overrides the
    /// defaults, and a missing or malformed one leaves them intact. When a tuning session
    /// settles, transcribe the accepted numbers back into the defaults in LayoutSpec and
    /// delete the file. A failed load leaves the previous spec in place: a half-parsed layout
    /// would be worse than the one already on screen.
    /// </summary>
    internal static class DevLayout
    {
        public static LayoutSpec Spec
        {
            get { return LayoutSpec.Current; }
            private set { LayoutSpec.Current = value; }
        }

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
            if (DevTools.LayoutPollSeconds == null) return;
            float interval = DevTools.LayoutPollSeconds.Value;
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
