using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

using Gameplay.Perks;
using HarmonyLib;

namespace Ectype
{
    /// <summary>
    /// Host-authoritative, live suppression of registered ectype classes - distinct from
    /// Suppression.cs's manifest-declared vanilla-tree suppression, which is static data
    /// resolved once. This is a runtime choice the host makes from the F5 menu (see
    /// EctypeConfig.HostSuppressedClassIds), synced to every client via a Photon room
    /// custom property (RoomSync.cs), and can change more than once per session.
    ///
    /// SetSuppressed is the UI-agnostic entry point: room-property sync calls it today, and
    /// a future in-hub terminal panel can call it directly instead - neither this type nor
    /// its consumers know anything about config or Photon.
    ///
    /// Live suppression never writes the profile. The perks stay in every saved loadout and
    /// SuppressionOverlay makes the game read them as absent, so lifting it restores the
    /// build exactly.
    ///
    /// Suppressing a class here never touches AbilityWiring's per-character install loop:
    /// removing an ability slot for only some clients desynchronises the position-indexed
    /// Photon ability array. The installed ability is deselected instead.
    /// </summary>
    internal static class HostSuppression
    {
        private static readonly HashSet<string> _suppressed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _armed;
        public static string Status = "nothing host-suppressed";

        public static bool IsClassSuppressed(string classId)
        {
            return !string.IsNullOrEmpty(classId) && _suppressed.Contains(classId);
        }

        /// <summary>
        /// Replace the host-suppressed class set. No-ops if unchanged, so RoomSync's poll
        /// can call this every tick for free. On an actual change: rederives vanilla-tree
        /// suppression (a class's own manifest.suppress entries only count while the class
        /// itself is not host-suppressed - see Suppression.OnHostSuppressionChanged), reloads
        /// every player's perks, rebuilds the gene-tree screens, and arms the one-way
        /// session-modded flag the first time anything is actually suppressed.
        /// </summary>
        public static void SetSuppressed(IEnumerable<string> classIds)
        {
            HashSet<string> next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (classIds != null)
                foreach (string id in classIds)
                    if (!string.IsNullOrEmpty(id)) next.Add(id);

            if (next.SetEquals(_suppressed)) return;

            StringBuilder added = new StringBuilder();
            StringBuilder removed = new StringBuilder();
            foreach (string id in next)
                if (!_suppressed.Contains(id)) (added.Length > 0 ? added.Append(", ") : added).Append(id);
            foreach (string id in _suppressed)
                if (!next.Contains(id)) (removed.Length > 0 ? removed.Append(", ") : removed).Append(id);

            _suppressed.Clear();
            foreach (string id in next) _suppressed.Add(id);

            Status = _suppressed.Count == 0
                ? "nothing host-suppressed"
                : _suppressed.Count + " class(es) host-suppressed: " +
                  string.Join(", ", new List<string>(_suppressed).ToArray());
            Plugin.Log.LogInfo("Ectype: host suppression changed" +
                               (added.Length > 0 ? " +[" + added + "]" : "") +
                               (removed.Length > 0 ? " -[" + removed + "]" : "") + ".");

            try { Suppression.OnHostSuppressionChanged(); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: suppression rederive after host-suppression " +
                                    "change failed: " + e);
            }

            ReapplyAllPlayerPerks();

            try { TreeRebuild.RebuildAll(); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: gene-tree rebuild after host-suppression change " +
                                    "failed: " + e);
            }

            if (_suppressed.Count > 0) Arm();
        }

        /// <summary>
        /// Re-apply every player's stats from their published PBL. Every client applies every
        /// player's perks itself, so the local player alone is not enough; and the ReApplyPerks
        /// prefix removes suppressed perks from the loader's decoded copy, so only a fresh
        /// decode brings them back when suppression lifts.
        ///
        /// ReApplyPerks raises the static OnPerkBuffLevelsUpdated, and every local class
        /// ability re-checks its selection on it - once per loader would be a crew-wide
        /// re-check per player for one change. The event is held for the batch and a single
        /// selection pass on the local character replaces it.
        /// </summary>
        private static void ReapplyAllPlayerPerks()
        {
            Action held = PlayerPerkLoader.OnPerkBuffLevelsUpdated;
            PlayerPerkLoader.OnPerkBuffLevelsUpdated = null;
            try
            {
                foreach (PlayerPerkLoader loader in UnityEngine.Object.FindObjectsOfType<PlayerPerkLoader>())
                {
                    try
                    {
                        if (loader.photonView == null || loader.photonView.Owner == null) continue;
                        object raw;
                        if (!loader.photonView.Owner.CustomProperties.TryGetValue("PBL", out raw)) continue;
                        int[] pbl = raw as int[];
                        if (pbl == null) continue;
                        loader.PerkBuffLevels = PlayerPerkLoader.DeserializePerkBuffLevels(pbl);
                        loader.ReApplyPerks();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Ectype: re-applying a player's perks after a host " +
                                            "suppression change failed: " + e);
                    }
                }
            }
            finally
            {
                PlayerPerkLoader.OnPerkBuffLevelsUpdated = held;
            }

            try { SuppressionOverlay.SyncLocalSelection(Adoption.LocalLoco); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: class-ability selection sync after a host " +
                                    "suppression change failed: " + e);
            }
        }

        /// <summary>
        /// One-way session flag: suppressing a class changes what every player in the room
        /// can do, so it must flag the session as modded, and never un-arms.
        /// </summary>
        private static void Arm()
        {
            if (_armed) return;
            _armed = true;
            try
            {
                Type ph = AccessTools.TypeByName("VoidManager.PluginHandler");
                MethodInfo escalate = ph == null ? null : ph.GetMethod("EscalateSession",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (escalate != null)
                {
                    escalate.Invoke(null, null);
                    Plugin.Log.LogInfo("Ectype: host suppression armed - session flagged " +
                                       "via VoidManager.PluginHandler.EscalateSession.");
                    return;
                }
                Plugin.Log.LogWarning("Ectype: PluginHandler.EscalateSession not found; " +
                                      "falling back to ModdingUtils.RegisterSessionMod.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: PluginHandler.EscalateSession threw (" +
                                      e.Message + "); falling back to ModdingUtils.");
            }

            try
            {
                ModdingUtils.RegisterSessionMod();
                Plugin.Log.LogInfo("Ectype: host suppression armed - session flagged via " +
                                   "ModdingUtils.RegisterSessionMod.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: could not flag the session for host " +
                                    "suppression: " + e);
            }
        }

    }
}
