using System;
using System.Collections.Generic;
using System.Text;

using ExitGames.Client.Photon;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Ectype
{
    /// <summary>Where the value a client holds for a key came from.</summary>
    internal enum HostRoomSource
    {
        /// <summary>Read with no writer named (the join response, or the room's copy at a
        /// registration), or kept after a room value this client could not read. Nothing vouches
        /// that the master wrote it.</summary>
        Unverified,
        /// <summary>Taken from a property change whose writer was the master client.</summary>
        Master,
        /// <summary>The host's own value: one it set, or one it kept when it became the
        /// host.</summary>
        Own,
    }

    /// <summary>Why the latest room write of a key, after the last one taken, was not
    /// taken.</summary>
    internal enum HostRoomRefusal
    {
        None,
        /// <summary>Its writer was not the master client.</summary>
        Writer,
        /// <summary>It was over the value's cap, not JSON text, or did not read as the value's
        /// type.</summary>
        Read,
    }

    /// <summary>What a client that has just become the host does with each key.</summary>
    internal sealed class HostRoomAdoption
    {
        /// <summary>Kept, and written back because the room holds them differently.</summary>
        internal readonly List<string> Back = new List<string>();
        /// <summary>Held unverified: cleared to none, and the none written.</summary>
        internal readonly List<string> Cleared = new List<string>();
        /// <summary>The room holds a value this client could not read: left as the room holds
        /// it.</summary>
        internal readonly List<string> Standing = new List<string>();
        /// <summary>Keys the host now holds a value of its own for.</summary>
        internal int Held;
    }

    /// <summary>What a client that has just stopped being the host does with each key.</summary>
    internal sealed class HostRoomRelease
    {
        /// <summary>Gone back to the last value the room took from the master, now held.</summary>
        internal readonly List<string> Reverted = new List<string>();
        /// <summary>No value the room took from the master is known: to be read from the room,
        /// unverified.</summary>
        internal readonly List<string> FromRoom = new List<string>();
    }

    /// <summary>
    /// Every host room value as this client holds it, with nothing of Photon or Unity in it. Per
    /// key: the compact JSON text this client goes by (null for none), where it came from, the
    /// last value this client saw the room take from the master, and why the latest room write
    /// was refused. The host writes its own values and goes by them, never by the room's copy,
    /// which changes a round trip after each write. Keys the host has to write wait in an ordered
    /// pending list until the next flush; a put-back after another player's write waits until
    /// <see cref="PutBackSeconds"/> after that key's previous put-back.
    /// </summary>
    internal sealed class HostRoomCopy
    {
        /// <summary>The shortest time between two put-backs of one key.</summary>
        internal const float PutBackSeconds = 0.5f;

        private sealed class Entry
        {
            internal string Held;
            internal HostRoomSource Source = HostRoomSource.Unverified;
            internal HostRoomRefusal Refused;
            internal bool HasMaster;
            internal string MasterText;
            internal bool Pending;
            internal bool PutBack;
            internal float Due;
            internal float LastPutBack = float.NegativeInfinity;
        }

        private readonly Dictionary<string, Entry> _keys = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _pending = new List<string>();

        /// <summary>The JSON text held for <paramref name="key"/>, or null for none.</summary>
        internal string Held(string key)
        {
            Entry e = Find(key);
            return e != null ? e.Held : null;
        }

        internal HostRoomSource Source(string key)
        {
            Entry e = Find(key);
            return e != null ? e.Source : HostRoomSource.Unverified;
        }

        internal HostRoomRefusal Refusal(string key)
        {
            Entry e = Find(key);
            return e != null ? e.Refused : HostRoomRefusal.None;
        }

        internal int PendingCount { get { return _pending.Count; } }

        /// <summary>A client takes a value it read from the room (null: none):
        /// <paramref name="master"/> when the change named the master as its writer, otherwise
        /// unverified. True when the held text changed.</summary>
        internal bool Took(string key, string json, bool master)
        {
            Entry e = Get(key);
            bool changed = !string.Equals(e.Held, json, StringComparison.Ordinal);
            e.Held = json;
            e.Source = master ? HostRoomSource.Master : HostRoomSource.Unverified;
            e.Refused = HostRoomRefusal.None;
            e.HasMaster = master;
            e.MasterText = master ? json : null;
            return changed;
        }

        /// <summary>A client did not take a room write of <paramref name="key"/>. After a read
        /// refusal the held value is unverified: the room now holds something else, which may be
        /// the master's.</summary>
        internal void Refused(string key, HostRoomRefusal why)
        {
            Entry e = Get(key);
            e.Refused = why;
            if (why != HostRoomRefusal.Read) return;
            e.Source = HostRoomSource.Unverified;
            e.HasMaster = false;
            e.MasterText = null;
        }

        /// <summary>The host writes a value: held at once as its own, and pending for the next
        /// flush. True when anything is to be written: the text changed, or the key was not yet
        /// the host's own.</summary>
        internal bool Write(string key, string json)
        {
            Entry e = Get(key);
            if (e.Source == HostRoomSource.Own && string.Equals(e.Held, json, StringComparison.Ordinal)) return false;
            e.Held = json;
            e.Source = HostRoomSource.Own;
            e.Refused = HostRoomRefusal.None;
            MarkPending(key, e, false, 0f);
            return true;
        }

        /// <summary>The host saw the room take its own write of <paramref name="key"/>: the
        /// room's value, <paramref name="raw"/>, is now one the master wrote.</summary>
        internal void Echo(string key, object raw)
        {
            if (raw != null && !(raw is string)) return;
            Entry e = Get(key);
            e.HasMaster = true;
            e.MasterText = (string)raw;
        }

        /// <summary>The host saw another player's write of <paramref name="key"/>, now
        /// <paramref name="raw"/> in the room. True when it differs from the host's own value,
        /// which is then pending as a put-back, due <see cref="PutBackSeconds"/> after the key's
        /// previous put-back. A key the host has not claimed is not guarded.</summary>
        internal bool Foreign(string key, object raw, float now)
        {
            Entry e = Find(key);
            if (e == null || e.Source != HostRoomSource.Own) return false;
            if (SameAsRoom(e.Held, raw)) return false;
            MarkPending(key, e, true, now);
            return true;
        }

        /// <summary>
        /// This client has just become the host. It vouches only for values a master wrote or it
        /// set itself. A value taken from the master's write is kept as its own and written back
        /// where the room holds it differently (a write refused because its writer was not the
        /// master). A key whose latest room value this client could not read is left as the room
        /// holds it, unguarded until the host sets it. Every other value is unverified: it is
        /// cleared to none and the none written, so every client converges on the default.
        /// </summary>
        internal HostRoomAdoption Adopt(IEnumerable<string> keys, Func<string, object> roomRaw)
        {
            HostRoomAdoption result = new HostRoomAdoption();
            foreach (string key in keys)
            {
                Entry e = Get(key);
                object raw = roomRaw(key);
                if (e.Source == HostRoomSource.Unverified && e.Refused == HostRoomRefusal.Read)
                {
                    if (raw != null) result.Standing.Add(key);
                    continue;
                }
                if (e.Source == HostRoomSource.Unverified)
                {
                    e.Source = HostRoomSource.Own;
                    e.Refused = HostRoomRefusal.None;
                    e.HasMaster = false;
                    e.MasterText = null;
                    if (e.Held == null && raw == null) continue;
                    e.Held = null;
                    MarkPending(key, e, false, 0f);
                    result.Cleared.Add(key);
                    continue;
                }
                e.Source = HostRoomSource.Own;
                e.Refused = HostRoomRefusal.None;
                if (e.Held != null) result.Held++;
                if (SameAsRoom(e.Held, raw)) continue;
                MarkPending(key, e, false, 0f);
                result.Back.Add(key);
            }
            return result;
        }

        /// <summary>
        /// This client has just stopped being the host. Its unsent writes are dropped, and every
        /// value of its own it has not seen the room take from it goes back to the last value the
        /// room took from the master, or, with none known, is to be read from the room as a joiner
        /// reads it. A write sent but sequenced after the master changed is refused by every
        /// client, so the former host goes back with them.
        /// </summary>
        internal HostRoomRelease Release(IEnumerable<string> keys)
        {
            foreach (string key in _pending) Find(key).Pending = false;
            _pending.Clear();
            HostRoomRelease result = new HostRoomRelease();
            foreach (string key in keys)
            {
                Entry e = Find(key);
                if (e == null || e.Source != HostRoomSource.Own) continue;
                if (!e.HasMaster)
                {
                    e.Source = HostRoomSource.Unverified;
                    result.FromRoom.Add(key);
                    continue;
                }
                e.Source = HostRoomSource.Master;
                if (string.Equals(e.Held, e.MasterText, StringComparison.Ordinal)) continue;
                e.Held = e.MasterText;
                result.Reverted.Add(key);
            }
            return result;
        }

        /// <summary>The pending keys due at <paramref name="now"/> with their held values, in the
        /// order first marked. They leave the pending list; the others stay.</summary>
        internal List<KeyValuePair<string, string>> TakeBatch(float now)
        {
            List<KeyValuePair<string, string>> batch = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < _pending.Count;)
            {
                string key = _pending[i];
                Entry e = Find(key);
                if (e.Due > now)
                {
                    i++;
                    continue;
                }
                _pending.RemoveAt(i);
                e.Pending = false;
                if (e.PutBack) e.LastPutBack = now;
                e.PutBack = false;
                batch.Add(new KeyValuePair<string, string>(key, e.Held));
            }
            return batch;
        }

        /// <summary>A batch that did not reach the room: its keys are pending again, due at once,
        /// ahead of any marked since, in their order.</summary>
        internal void Requeue(List<KeyValuePair<string, string>> batch)
        {
            int at = 0;
            foreach (KeyValuePair<string, string> kv in batch)
            {
                Entry e = Get(kv.Key);
                e.PutBack = false;
                e.Due = float.NegativeInfinity;
                if (e.Pending) _pending.Remove(kv.Key);
                e.Pending = true;
                _pending.Insert(at++, kv.Key);
            }
        }

        /// <summary>UTF-8 bytes of every held value, with <paramref name="key"/>'s replaced by
        /// <paramref name="json"/>.</summary>
        internal int TotalBytesWith(string key, string json)
        {
            int total = SessionData.Bytes(json);
            foreach (KeyValuePair<string, Entry> kv in _keys)
                if (!string.Equals(kv.Key, key, StringComparison.Ordinal)) total += SessionData.Bytes(kv.Value.Held);
            return total;
        }

        internal void Clear()
        {
            _keys.Clear();
            _pending.Clear();
        }

        private Entry Find(string key)
        {
            Entry e;
            return key != null && _keys.TryGetValue(key, out e) ? e : null;
        }

        private Entry Get(string key)
        {
            Entry e = Find(key);
            if (e == null) _keys[key] = e = new Entry();
            return e;
        }

        /// <summary>Pending: an own write is due at once, and takes over a waiting put-back; a
        /// put-back is due <see cref="PutBackSeconds"/> after the key's previous one.</summary>
        private void MarkPending(string key, Entry e, bool putBack, float now)
        {
            if (!putBack)
            {
                e.PutBack = false;
                e.Due = float.NegativeInfinity;
            }
            if (e.Pending) return;
            if (putBack)
            {
                e.PutBack = true;
                e.Due = Math.Max(now, e.LastPutBack + PutBackSeconds);
            }
            e.Pending = true;
            _pending.Add(key);
        }

        /// <summary>True when the room's property value is the held text: the same string, or
        /// absent (or null) for none.</summary>
        internal static bool SameAsRoom(string held, object raw)
        {
            if (raw == null) return held == null;
            string s = raw as string;
            return s != null && string.Equals(s, held, StringComparison.Ordinal);
        }

        /// <summary>A client takes a property change written by <paramref name="sender"/> when it
        /// is the master as the change arrives. A change the server made names actor 0 and is
        /// taken too; an event that names no writer at all (-1) is not.</summary>
        internal static bool WriterAccepted(int sender, int master)
        {
            return sender == 0 || sender == master;
        }
    }

    internal interface IHostRoomValue
    {
        string Key { get; }
        int Cap { get; }
        string TypeName { get; }
        /// <summary>Holds the value <paramref name="json"/> decodes to (null: the default).
        /// False, holding nothing new, when it does not read as the value's type.</summary>
        bool Take(string json, out string error);
        void RaiseIfChanged();
    }

    /// <summary>
    /// The channel of every <see cref="HostRoomValue{T}"/>: one room property per value, named
    /// <see cref="PropertyPrefix"/> plus its key, holding the value's compact JSON. The host goes
    /// by its own copy and writes the values changed in a frame in one room update, at the end of
    /// the frame. A client takes a change only when Photon reports the master as its writer; the
    /// host writes its own value back when anyone else writes one. A player who becomes the master
    /// keeps the values a master wrote and clears the ones it holds unverified.
    /// </summary>
    internal static class HostRoomValues
    {
        internal const string PropertyPrefix = SessionData.RoomPropertyPrefix + "Value:";
        internal const int TotalCapBytes = SessionData.EnvelopeCapBytes;

        private static readonly List<IHostRoomValue> _values = new List<IHostRoomValue>();
        private static readonly Dictionary<string, IHostRoomValue> _byKey =
            new Dictionary<string, IHostRoomValue>(StringComparer.Ordinal);
        private static readonly HostRoomCopy _copy = new HostRoomCopy();
        private static readonly HashSet<string> _once = new HashSet<string>(StringComparer.Ordinal);

        private static Room _room;
        private static bool _host;
        private static int _master;
        private static bool _callbacks;
        private static int _lastEndOfFrame = -1;

        internal static int Count { get { return _values.Count; } }

        internal static bool IsRegistered(string key) { return _byKey.ContainsKey(key); }

        internal static void Register(IHostRoomValue v)
        {
            _values.Add(v);
            _byKey[v.Key] = v;
            if (_room == null) return;
            Read(v, Raw(_room, v.Key), false);
            if (_host) Claim(new[] { v.Key }, _room, false);
            v.RaiseIfChanged();
        }

        internal static bool CanSet
        {
            get
            {
                SyncRole();
                return _host;
            }
        }

        internal enum WriteResult { Written, Claimed, Unchanged, NotHost, OverTotal }

        /// <summary>Host: holds <paramref name="json"/> as <paramref name="v"/>'s value, pending for
        /// the next flush. <see cref="WriteResult.Claimed"/>: the text was already held, but not as
        /// the host's own, and is written.</summary>
        internal static WriteResult Write(IHostRoomValue v, string json, out int total)
        {
            total = 0;
            SyncRole();
            if (!_host) return WriteResult.NotHost;
            total = _copy.TotalBytesWith(v.Key, json);
            if (total > TotalCapBytes) return WriteResult.OverTotal;
            bool same = string.Equals(_copy.Held(v.Key), json, StringComparison.Ordinal);
            if (!_copy.Write(v.Key, json)) return WriteResult.Unchanged;
            return same ? WriteResult.Claimed : WriteResult.Written;
        }

        internal static bool Once(string what) { return _once.Add(what); }

        // ------------------------------------------------------------------ driver

        /// <summary>Every frame, from the driver's <c>Update</c>: follows the room and the master
        /// role. Sends the pending values itself only when the end-of-frame step did not run last
        /// frame.</summary>
        internal static void Poll()
        {
            if (_values.Count == 0) return;
            if (!_callbacks)
            {
                _callbacks = true;
                try { PhotonNetwork.AddCallbackTarget(RoomCallbacks.Instance); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Ectype: host room values: the room callbacks could not be added; joins and " +
                                          "host changes are noticed at the next frame: " + e.Message);
                }
            }
            try
            {
                SyncRole();
                if (Time.frameCount - _lastEndOfFrame > 1) Flush();
            }
            catch (Exception e)
            {
                if (_once.Add("poll"))
                    Plugin.Log.LogError("Ectype: host room values: update failed (reported once): " + e);
            }
        }

        /// <summary>At the end of every frame, after every <c>Update</c>, <c>LateUpdate</c>,
        /// coroutine and <c>OnGUI</c> of the frame: sends the host's pending values in one room
        /// update.</summary>
        internal static void EndOfFrame()
        {
            _lastEndOfFrame = Time.frameCount;
            if (_values.Count == 0) return;
            try
            {
                SyncRole();
                Flush();
            }
            catch (Exception e)
            {
                if (_once.Add("poll"))
                    Plugin.Log.LogError("Ectype: host room values: update failed (reported once): " + e);
            }
        }

        private static void Flush()
        {
            if (!_host || _room == null || _copy.PendingCount == 0) return;
            List<KeyValuePair<string, string>> batch = _copy.TakeBatch(Time.unscaledTime);
            if (batch.Count == 0) return;
            ExitGames.Client.Photon.Hashtable changes = new ExitGames.Client.Photon.Hashtable();
            StringBuilder keys = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in batch)
            {
                changes[PropertyPrefix + kv.Key] = kv.Value;
                if (keys.Length > 0) keys.Append(", ");
                keys.Append(kv.Key);
            }
            string problem = null;
            try
            {
                if (!_room.SetCustomProperties(changes)) problem = "Photon did not send it";
            }
            catch (Exception e)
            {
                problem = e.Message;
            }
            if (problem != null)
            {
                _copy.Requeue(batch);
                if (_once.Add("unsent"))
                    Plugin.Log.LogWarning("Ectype: host room values: the room update of " + keys + " was not sent (" +
                                          problem + "); the host sends it again at the next frame.");
                return;
            }
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: host room values: wrote " + batch.Count + " value(s) in one room update (" +
                                   keys + ").");
        }

        /// <summary>Brings the channel in line with the room this client is in and whether it is
        /// the master. Called from the room callbacks, every frame, and before any read or write
        /// that depends on the role.</summary>
        private static void SyncRole()
        {
            Room room = PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
            if (room == null)
            {
                if (_room != null) Left();
                return;
            }
            if (!ReferenceEquals(room, _room)) Joined(room);
            NoteMaster(room);
            bool master = PhotonNetwork.IsMasterClient;
            if (master && !_host) BecameHost(room);
            else if (!master && _host) LostHost(room);
        }

        /// <summary>Photon moves the master role on its own only when the master leaves; it has
        /// already removed the departed master (or marked it inactive) when the switch is reported.
        /// A switch while the previous master is still active in the room was asked for by a
        /// player, and the writer rule follows it.</summary>
        private static void NoteMaster(Room room)
        {
            int now = room.MasterClientId;
            if (now == _master) return;
            int before = _master;
            _master = now;
            if (before <= 0) return;
            Photon.Realtime.Player previous = room.GetPlayer(before);
            if (previous == null || previous.IsInactive) return;
            Plugin.Log.LogWarning("Ectype: host room values: the master client moved from actor " + before +
                                  " to actor " + now + " while " + before + " is still in the room; clients now " +
                                  "take host room values written by " + now + ".");
        }

        /// <summary>A joiner reads every value from the room as it joins. The join response names
        /// no writer, so what the room holds is taken unverified.</summary>
        private static void Joined(Room room)
        {
            if (_room != null) Left();
            _room = room;
            _host = false;
            _master = room.MasterClientId;
            foreach (IHostRoomValue v in _values) Read(v, Raw(room, v.Key), false);
            RaiseAll();
        }

        private static void Left()
        {
            foreach (IHostRoomValue v in _values)
            {
                if (_copy.Held(v.Key) == null) continue;
                string ignored;
                v.Take(null, out ignored);
            }
            _copy.Clear();
            _room = null;
            _host = false;
            _master = 0;
            _once.Clear();
            RaiseAll();
        }

        private static void BecameHost(Room room)
        {
            _host = true;
            Claim(new List<string>(_byKey.Keys), room, true);
            RaiseAll();
        }

        /// <summary>The host takes <paramref name="keys"/> as its own, by the adoption rule of
        /// <see cref="HostRoomCopy.Adopt"/>.</summary>
        private static void Claim(IEnumerable<string> keys, Room room, bool log)
        {
            HostRoomAdoption a = _copy.Adopt(keys, k => Raw(room, k));
            foreach (string key in a.Cleared)
            {
                IHostRoomValue v;
                string ignored;
                if (_byKey.TryGetValue(key, out v)) v.Take(null, out ignored);
            }
            if (a.Cleared.Count > 0)
                Plugin.Log.LogWarning("Ectype: host room values: this player hosts the room now and clears " +
                                      a.Cleared.Count + " value(s) no master write vouched for (" +
                                      string.Join(", ", a.Cleared.ToArray()) + ").");
            if (a.Standing.Count > 0)
                Plugin.Log.LogWarning("Ectype: host room values: this player hosts the room now and leaves " +
                                      a.Standing.Count + " value(s) it could not read as the room holds them (" +
                                      string.Join(", ", a.Standing.ToArray()) + ").");
            if (!log) return;
            if (a.Back.Count > 0)
                Plugin.Log.LogWarning("Ectype: host room values: this player hosts the room now and keeps the " + a.Held +
                                      " value(s) it held; " + a.Back.Count + " differed in the room and are put back (" +
                                      string.Join(", ", a.Back.ToArray()) + ").");
            else if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: host room values: this player hosts the room now and keeps the " + a.Held +
                                   " value(s) it held.");
        }

        /// <summary>Values of its own the former host never saw the room take go back to the last
        /// value the room took from the master.</summary>
        private static void LostHost(Room room)
        {
            _host = false;
            HostRoomRelease r = _copy.Release(new List<string>(_byKey.Keys));
            foreach (string key in r.Reverted)
            {
                IHostRoomValue v;
                if (!_byKey.TryGetValue(key, out v)) continue;
                string problem;
                if (!v.Take(_copy.Held(key), out problem) && _once.Add("revert:" + key))
                    Plugin.Log.LogWarning("Ectype: host room value '" + key + "': the room's value does not read as " +
                                          v.TypeName + " within " + v.Cap + " bytes (" + problem + "); this client " +
                                          "keeps the value it had.");
            }
            foreach (string key in r.FromRoom)
            {
                IHostRoomValue v;
                if (_byKey.TryGetValue(key, out v)) Read(v, Raw(room, key), false);
            }
            RaiseAll();
        }

        /// <summary>
        /// A room property change, from <c>LoadBalancingClient.EventReceived</c> (event 253, after
        /// Photon has merged it into the room's copy). <paramref name="sender"/> is the actor whose
        /// operation made the change, stamped by the Photon server.
        /// </summary>
        internal static void OnPropertiesChanged(int sender, ExitGames.Client.Photon.Hashtable props)
        {
            if (_values.Count == 0 || props == null) return;
            try
            {
                List<IHostRoomValue> touched = null;
                foreach (System.Collections.DictionaryEntry entry in props)
                {
                    string name = entry.Key as string;
                    if (name == null || !name.StartsWith(PropertyPrefix, StringComparison.Ordinal)) continue;
                    IHostRoomValue v;
                    if (!_byKey.TryGetValue(name.Substring(PropertyPrefix.Length), out v)) continue;
                    if (touched == null) touched = new List<IHostRoomValue>();
                    touched.Add(v);
                }
                if (touched == null) return;
                SyncRole();
                Room room = _room;
                if (room == null) return;

                if (_host)
                {
                    Photon.Realtime.Player me = PhotonNetwork.LocalPlayer;
                    if (me != null && sender == me.ActorNumber)
                    {
                        foreach (IHostRoomValue v in touched) _copy.Echo(v.Key, props[PropertyPrefix + v.Key]);
                        return;
                    }
                    List<string> back = new List<string>();
                    float now = Time.unscaledTime;
                    foreach (IHostRoomValue v in touched)
                        if (_copy.Foreign(v.Key, props[PropertyPrefix + v.Key], now)) back.Add(v.Key);
                    if (back.Count > 0 && _once.Add("back:" + sender))
                        Plugin.Log.LogWarning("Ectype: host room values: actor " + sender + " wrote " +
                                              string.Join(", ", back.ToArray()) + "; the host puts its own back.");
                    return;
                }

                int master = room.MasterClientId;
                if (!HostRoomCopy.WriterAccepted(sender, master))
                {
                    List<string> keys = new List<string>();
                    foreach (IHostRoomValue v in touched)
                    {
                        _copy.Refused(v.Key, HostRoomRefusal.Writer);
                        keys.Add(v.Key);
                    }
                    if (_once.Add("foreign:" + sender))
                        Plugin.Log.LogWarning("Ectype: host room values: ignored a write of " + string.Join(", ", keys.ToArray()) +
                                              " from actor " + sender + ", who is not the master client (" + master + ").");
                    return;
                }
                foreach (IHostRoomValue v in touched) Read(v, props[PropertyPrefix + v.Key], true);
                foreach (IHostRoomValue v in touched) v.RaiseIfChanged();
            }
            catch (Exception e)
            {
                if (_once.Add("event"))
                    Plugin.Log.LogError("Ectype: host room values: reading a room update failed (reported once): " + e);
            }
        }

        /// <summary>Takes a room property value as <paramref name="v"/>'s when it is absent, null,
        /// or a string within the value's cap that reads as its type: from the master's write when
        /// <paramref name="master"/>, otherwise unverified. Anything else is refused, and the
        /// value this client had is kept, unverified.</summary>
        private static void Read(IHostRoomValue v, object raw, bool master)
        {
            string json = null;
            string problem = null;
            if (raw != null)
            {
                json = raw as string;
                if (json == null) problem = "a " + raw.GetType().Name + ", not JSON text";
                else if (SessionData.Bytes(json) > v.Cap) problem = SessionData.Bytes(json) + " bytes";
            }
            if (problem == null)
            {
                if (string.Equals(_copy.Held(v.Key), json, StringComparison.Ordinal) || v.Take(json, out problem))
                {
                    _copy.Took(v.Key, json, master);
                    return;
                }
            }
            _copy.Refused(v.Key, HostRoomRefusal.Read);
            if (_once.Add("read:" + v.Key))
                Plugin.Log.LogWarning("Ectype: host room value '" + v.Key + "': the room's value does not read as " +
                                      v.TypeName + " within " + v.Cap + " bytes (" + problem + "); this client keeps " +
                                      "the value it had.");
        }

        /// <summary>Raises every changed value. A subscriber may register another value meanwhile,
        /// so the list is copied first.</summary>
        private static void RaiseAll()
        {
            foreach (IHostRoomValue v in _values.ToArray()) v.RaiseIfChanged();
        }

        private static object Raw(Room room, string key)
        {
            object raw;
            return room != null && room.CustomProperties != null &&
                   room.CustomProperties.TryGetValue(PropertyPrefix + key, out raw) ? raw : null;
        }

        /// <summary>Joins, leaves and master changes, as Photon reports them, before any event of
        /// the new state is dispatched.</summary>
        private sealed class RoomCallbacks : IMatchmakingCallbacks, IInRoomCallbacks
        {
            internal static readonly RoomCallbacks Instance = new RoomCallbacks();

            private static void Sync()
            {
                try { SyncRole(); }
                catch (Exception e)
                {
                    if (_once.Add("callback"))
                        Plugin.Log.LogError("Ectype: host room values: update failed (reported once): " + e);
                }
            }

            public void OnJoinedRoom() { Sync(); }
            public void OnLeftRoom() { Sync(); }
            public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient) { Sync(); }
            public void OnCreatedRoom() { }
            public void OnFriendListUpdate(List<FriendInfo> friendList) { }
            public void OnCreateRoomFailed(short returnCode, string message) { }
            public void OnJoinRoomFailed(short returnCode, string message) { }
            public void OnJoinRandomFailed(short returnCode, string message) { }
            public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer) { }
            public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer) { }
            public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
            public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer,
                                                 ExitGames.Client.Photon.Hashtable changedProps) { }
        }
    }

    /// <summary>
    /// A value only the host writes, held in a room property and read on every client. Get one
    /// from <see cref="SessionData.HostRoomValue{T}"/>. It lives as long as the room, in the hub
    /// and in a mission alike: a session start neither sends nor clears it.
    /// </summary>
    public sealed class HostRoomValue<T> : IHostRoomValue
    {
        private readonly bool _live;
        private readonly T _default;
        private T _value;
        private bool _has;
        private bool _changed;

        internal HostRoomValue(string key, T defaultValue, int cap, bool live)
        {
            Key = key;
            MaxBytes = cap;
            _default = defaultValue;
            _value = defaultValue;
            _live = live;
        }

        /// <summary>The key, <c>"&lt;plugin GUID&gt;/&lt;name&gt;"</c>. The room property is
        /// <c>Ectype.Value:</c> followed by it.</summary>
        public string Key { get; private set; }

        /// <summary>The largest compact JSON size of the value, in UTF-8 bytes.</summary>
        public int MaxBytes { get; private set; }

        int IHostRoomValue.Cap { get { return MaxBytes; } }
        string IHostRoomValue.TypeName { get { return typeof(T).Name; } }

        /// <summary>The host's value on every client, the host included; the default given at
        /// registration until the host has written one, and again outside a room. The object is
        /// shared by every reader: build a new value and <see cref="Set"/> it rather than changing
        /// it in place.</summary>
        public T Value { get { return _value; } }

        /// <summary>True when <see cref="Value"/> is one the host wrote; false while it is the
        /// default.</summary>
        public bool HasValue { get { return _has; } }

        /// <summary>True when <see cref="Set"/> would write: this client is the master client of
        /// a room and the value was registered.</summary>
        public bool CanSet { get { return _live && HostRoomValues.CanSet; } }

        /// <summary>Raised with the new value when <see cref="Value"/> changes on this client: on
        /// the host inside <see cref="Set"/>, on a client once the whole room update that carried
        /// it has been read, and on leaving the room. Each subscriber runs in its own
        /// try/catch.</summary>
        public event Action<T> Changed;

        /// <summary>
        /// Host only: makes <paramref name="value"/> this value on every client. The host's
        /// <see cref="Value"/> changes at once; every value set during a frame leaves in one room
        /// update at the end of that frame. False, writing nothing, on a client, outside a room,
        /// for a value that was refused at registration, over <see cref="MaxBytes"/>, over the
        /// 16384-byte total of every host room value, or one that does not read back as
        /// <typeparamref name="T"/>. The value is copied: changing the object afterwards changes
        /// nothing.
        /// </summary>
        public bool Set(T value)
        {
            if (!_live || !HostRoomValues.CanSet) return false;
            string json;
            T copy;
            string problem = Encode(value, MaxBytes, out json, out copy);
            if (problem != null)
            {
                if (HostRoomValues.Once("set:" + Key + ":" + problem))
                    Plugin.Log.LogError("Ectype: host room value '" + Key + "' not written: " + problem + ".");
                return false;
            }
            int total;
            switch (HostRoomValues.Write(this, json, out total))
            {
                case HostRoomValues.WriteResult.NotHost:
                    return false;
                case HostRoomValues.WriteResult.OverTotal:
                    if (HostRoomValues.Once("total:" + Key))
                        Plugin.Log.LogError("Ectype: host room value '" + Key + "' not written: it would bring the host " +
                                            "room values to " + total + " bytes, over the " + HostRoomValues.TotalCapBytes +
                                            "-byte cap.");
                    return false;
                case HostRoomValues.WriteResult.Unchanged:
                case HostRoomValues.WriteResult.Claimed:
                    return true;
            }
            Hold(copy, true);
            RaiseIfChanged();
            return true;
        }

        /// <summary>The compact JSON of <paramref name="value"/> and a copy read back from it, or
        /// why it cannot be a host room value of at most <paramref name="cap"/> bytes.</summary>
        internal static string Encode(T value, int cap, out string json, out T copy)
        {
            json = null;
            copy = default(T);
            try
            {
                json = value == null ? "null" : JToken.FromObject(value, SessionData.Json).ToString(Formatting.None);
            }
            catch (Exception e)
            {
                return "it could not be serialised (" + e.Message + ")";
            }
            int size = SessionData.Bytes(json);
            if (size > cap) return "it is " + size + " bytes, over its " + cap + "-byte cap";
            string error;
            if (!Decode(json, out copy, out error))
                return "it does not read back as " + typeof(T).Name + " (" + error + ")";
            return null;
        }

        internal static bool Decode(string json, out T value, out string error)
        {
            value = default(T);
            error = null;
            try
            {
                value = SessionData.ParseJson(json).ToObject<T>(SessionData.Json);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        bool IHostRoomValue.Take(string json, out string error)
        {
            error = null;
            if (json == null)
            {
                Hold(_default, false);
                return true;
            }
            T value;
            if (!Decode(json, out value, out error)) return false;
            Hold(value, true);
            return true;
        }

        private void Hold(T value, bool has)
        {
            _value = value;
            _has = has;
            _changed = true;
        }

        void IHostRoomValue.RaiseIfChanged() { RaiseIfChanged(); }

        private void RaiseIfChanged()
        {
            if (!_changed) return;
            _changed = false;
            Action<T> handlers = Changed;
            if (handlers == null) return;
            T value = _value;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<T>)d)(value); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: host room value '" + Key + "' Changed subscriber " +
                                        d.Method.DeclaringType + "." + d.Method.Name + " threw: " + e);
                }
            }
        }
    }
}
