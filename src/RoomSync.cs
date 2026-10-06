using System;
using System.Collections.Generic;

using ExitGames.Client.Photon;
using Photon.Pun;

namespace Ectype
{
    /// <summary>
    /// Publishes the host's EctypeConfig.HostSuppressedClassIds to a Photon room custom
    /// property while they're in the hub, and reads it back on every client (including the
    /// host's own) - the one channel this framework uses to sync a host's live choice to the
    /// whole room. Polled from EctypeDriver.Update() rather than an IInRoomCallbacks
    /// subscription, matching this codebase's poll-and-diff convention.
    ///
    /// Outside a room nothing is host-suppressed.
    /// </summary>
    internal static class RoomSync
    {
        private const string RoomPropertyKey = "EctypeHostSuppress";

        private static float _nextPoll;
        private static string _lastPublished;   // what WE last wrote (host only)
        private static string _lastSeen;        // what we last read (every client)

        public static void Poll()
        {
            if (UnityEngine.Time.unscaledTime < _nextPoll) return;
            _nextPoll = UnityEngine.Time.unscaledTime + 1f;

            bool inHub;
            try { inHub = Gameplay.Hub.HubQuestManager.Instance != null; }
            catch { inHub = false; }

            if (inHub) PublishIfHost();
            ReadRoomProperty();
        }

        private static void PublishIfHost()
        {
            try
            {
                if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient) return;
                string current = CurrentTogglesAsCsv();
                if (current == _lastPublished) return;

                Hashtable props = new Hashtable();
                props[RoomPropertyKey] = current;
                PhotonNetwork.CurrentRoom.SetCustomProperties(props);
                _lastPublished = current;
                Plugin.Log.LogInfo("Ectype: published host suppression to the room" +
                                   (current.Length == 0 ? " (cleared)." : ": '" + current + "'."));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: publishing host suppression failed: " + e);
            }
        }

        /// <summary>Every checked [ClassSuppression] toggle, joined - the host's own live
        /// picture of what should be suppressed, independent of what's currently published.</summary>
        private static string CurrentTogglesAsCsv()
        {
            List<string> ids = new List<string>();
            foreach (KeyValuePair<string, BepInEx.Configuration.ConfigEntry<bool>> kv in
                     EctypeConfig.ClassSuppressToggles)
                if (kv.Value != null && kv.Value.Value) ids.Add(kv.Key);
            return string.Join(",", ids.ToArray());
        }

        private static void ReadRoomProperty()
        {
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null)
                {
                    // The next room starts with nothing published, so a host reopening with
                    // unchanged toggles must publish again.
                    _lastPublished = null;
                    if (_lastSeen != null)
                    {
                        _lastSeen = null;
                        HostSuppression.SetSuppressed(null);
                    }
                    return;
                }

                object raw;
                string value = PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(
                    RoomPropertyKey, out raw) ? raw as string : "";
                value = value ?? "";
                if (value == _lastSeen) return;
                _lastSeen = value;

                List<string> ids = new List<string>();
                if (value.Length > 0)
                    foreach (string id in value.Split(','))
                    {
                        string t = id.Trim();
                        if (t.Length > 0) ids.Add(t);
                    }
                HostSuppression.SetSuppressed(ids);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: reading host suppression room property failed: " + e);
            }
        }
    }
}
