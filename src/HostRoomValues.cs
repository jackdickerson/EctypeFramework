using System;
using System.Collections.Generic;
using System.Text;

using ExitGames.Client.Photon;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;

namespace Ectype
{
    /// <summary>
    /// The host's own copy of every host room value, with nothing of Photon or Unity in it. Each
    /// key holds the compact JSON text this client goes by, or null for none. A client fills it
    /// from writes the master made; the host writes it itself and goes by it, never by the room's
    /// copy, which changes a round trip after each write. Keys the host has to write to the room
    /// wait in an ordered pending list until the next flush, so changes made in one frame leave
    /// in one room update.
    /// </summary>
    internal sealed class HostRoomCopy
    {
        private readonly Dictionary<string, string> _held = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _pending = new List<string>();

        /// <summary>The JSON text held for <paramref name="key"/>, or null for none.</summary>
        internal string Held(string key)
        {
            string json;
            return key != null && _held.TryGetValue(key, out json) ? json : null;
        }

        internal int PendingCount { get { return _pending.Count; } }

        /// <summary>A client takes a value the master wrote (null: none). True when it changed.</summary>
        internal bool Accept(string key, string json)
        {
            if (string.Equals(Held(key), json, StringComparison.Ordinal)) return false;
            if (json == null) _held.Remove(key);
            else _held[key] = json;
            return true;
        }

        /// <summary>The host writes a value: held at once, and pending for the next flush. True
        /// when it changed; a value equal to the held one writes nothing.</summary>
        internal bool Write(string key, string json)
        {
            if (!Accept(key, json)) return false;
            MarkPending(key);
            return true;
        }

        /// <summary>
        /// This client has just become the host: it keeps every value it held as its own, and each
        /// key whose room copy differs from it is pending, to be written back. The held values are
        /// the room's own except where this client refused a write by a player who was not the
        /// master, so a forged value the departed host never put back is put back now. Returns the
        /// pending keys.
        /// </summary>
        internal List<string> Adopt(IEnumerable<string> keys, Func<string, object> roomRaw)
        {
            List<string> back = new List<string>();
            foreach (string key in keys)
            {
                if (SameAsRoom(Held(key), roomRaw(key))) continue;
                MarkPending(key);
                back.Add(key);
            }
            return back;
        }

        /// <summary>The host saw another player's write of <paramref name="key"/>, now
        /// <paramref name="raw"/> in the room. True (and pending) when it differs from the held
        /// value.</summary>
        internal bool Foreign(string key, object raw)
        {
            if (SameAsRoom(Held(key), raw)) return false;
            MarkPending(key);
            return true;
        }

        /// <summary>The pending keys with their held values, in the order first marked; the
        /// pending list is emptied.</summary>
        internal List<KeyValuePair<string, string>> TakeBatch()
        {
            List<KeyValuePair<string, string>> batch = new List<KeyValuePair<string, string>>(_pending.Count);
            foreach (string key in _pending) batch.Add(new KeyValuePair<string, string>(key, Held(key)));
            _pending.Clear();
            return batch;
        }

        /// <summary>This client stopped being the host before a flush: the pending keys, which
        /// never reached the room. The list is emptied.</summary>
        internal List<string> DropPending()
        {
            List<string> dropped = new List<string>(_pending);
            _pending.Clear();
            return dropped;
        }

        /// <summary>UTF-8 bytes of every held value, with <paramref name="key"/>'s replaced by
        /// <paramref name="json"/>.</summary>
        internal int TotalBytesWith(string key, string json)
        {
            int total = SessionData.Bytes(json);
            foreach (KeyValuePair<string, string> kv in _held)
                if (!string.Equals(kv.Key, key, StringComparison.Ordinal)) total += SessionData.Bytes(kv.Value);
            return total;
        }

        internal void Clear()
        {
            _held.Clear();
            _pending.Clear();
        }

        private void MarkPending(string key)
        {
            if (!_pending.Contains(key)) _pending.Add(key);
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
        /// is the master as the change arrives. Photon reports no writer (0 or less) for a change
        /// the server made, which is taken too.</summary>
        internal static bool WriterAccepted(int sender, int master)
        {
            return sender <= 0 || sender == master;
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
    /// by its own copy and writes the values changed in a frame in one room update. A client takes
    /// a change only when Photon reports the master as its writer; the host writes its own value
    /// back when anyone else writes one. A player who becomes the master keeps the values it held
    /// and puts back any the room holds differently.
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
        private static bool _callbacks;

        internal static int Count { get { return _values.Count; } }

        internal static bool IsRegistered(string key) { return _byKey.ContainsKey(key); }

        internal static void Register(IHostRoomValue v)
        {
            _values.Add(v);
            _byKey[v.Key] = v;
            if (_room == null) return;
            Accept(v, Raw(_room, v.Key));
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

        internal enum WriteResult { Written, Unchanged, NotHost, OverTotal }

        /// <summary>Host: holds <paramref name="json"/> as <paramref name="v"/>'s value, pending for
        /// the next flush.</summary>
        internal static WriteResult Write(IHostRoomValue v, string json, out int total)
        {
            total = 0;
            SyncRole();
            if (!_host) return WriteResult.NotHost;
            total = _copy.TotalBytesWith(v.Key, json);
            if (total > TotalCapBytes) return WriteResult.OverTotal;
            return _copy.Write(v.Key, json) ? WriteResult.Written : WriteResult.Unchanged;
        }

        internal static bool Once(string what) { return _once.Add(what); }

        // ------------------------------------------------------------------ driver

        /// <summary>Every frame: follows the room and the master role, and sends the host's
        /// pending values in one room update.</summary>
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
            List<KeyValuePair<string, string>> batch = _copy.TakeBatch();
            ExitGames.Client.Photon.Hashtable changes = new ExitGames.Client.Photon.Hashtable();
            StringBuilder keys = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in batch)
            {
                changes[PropertyPrefix + kv.Key] = kv.Value;
                if (keys.Length > 0) keys.Append(", ");
                keys.Append(kv.Key);
            }
            _room.SetCustomProperties(changes);
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
            bool master = PhotonNetwork.IsMasterClient;
            if (master && !_host) BecameHost(room);
            else if (!master && _host) LostHost(room);
        }

        /// <summary>A joiner reads every value from the room as it joins. The join response names
        /// no writer, so what the room holds is taken.</summary>
        private static void Joined(Room room)
        {
            if (_room != null) Left();
            _room = room;
            _host = false;
            foreach (IHostRoomValue v in _values) Accept(v, Raw(room, v.Key));
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
            _once.Clear();
            RaiseAll();
        }

        private static void BecameHost(Room room)
        {
            _host = true;
            List<string> keys = new List<string>(_byKey.Keys);
            List<string> back = _copy.Adopt(keys, k => Raw(room, k));
            int held = 0;
            foreach (string k in keys) if (_copy.Held(k) != null) held++;
            if (back.Count == 0)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: host room values: this player hosts the room now and keeps the " + held +
                                       " value(s) it held.");
            }
            else
                Plugin.Log.LogWarning("Ectype: host room values: this player hosts the room now and keeps the " + held +
                                      " value(s) it held; " + back.Count + " differed in the room and are put back (" +
                                      string.Join(", ", back.ToArray()) + ").");
        }

        /// <summary>Values the host held but never sent go back to what the room holds.</summary>
        private static void LostHost(Room room)
        {
            _host = false;
            foreach (string key in _copy.DropPending())
            {
                IHostRoomValue v;
                if (_byKey.TryGetValue(key, out v)) Accept(v, Raw(room, key));
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
                    if (me != null && sender == me.ActorNumber) return; // the host's own write
                    List<string> back = new List<string>();
                    foreach (IHostRoomValue v in touched)
                        if (_copy.Foreign(v.Key, props[PropertyPrefix + v.Key])) back.Add(v.Key);
                    if (back.Count > 0 && _once.Add("back:" + sender))
                        Plugin.Log.LogWarning("Ectype: host room values: actor " + sender + " wrote " +
                                              string.Join(", ", back.ToArray()) + "; the host puts its own back.");
                    return;
                }

                int master = room.MasterClientId;
                if (!HostRoomCopy.WriterAccepted(sender, master))
                {
                    if (_once.Add("foreign:" + sender))
                    {
                        List<string> keys = new List<string>();
                        foreach (IHostRoomValue v in touched) keys.Add(v.Key);
                        Plugin.Log.LogWarning("Ectype: host room values: ignored a write of " + string.Join(", ", keys.ToArray()) +
                                              " from actor " + sender + ", who is not the master client (" + master + ").");
                    }
                    return;
                }
                foreach (IHostRoomValue v in touched) Accept(v, props[PropertyPrefix + v.Key]);
                foreach (IHostRoomValue v in touched) v.RaiseIfChanged();
            }
            catch (Exception e)
            {
                if (_once.Add("event"))
                    Plugin.Log.LogError("Ectype: host room values: reading a room update failed (reported once): " + e);
            }
        }

        /// <summary>Takes a room property value as <paramref name="v"/>'s, when it is absent, null,
        /// or a string within the value's cap that reads as its type.</summary>
        private static void Accept(IHostRoomValue v, object raw)
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
                if (string.Equals(_copy.Held(v.Key), json, StringComparison.Ordinal)) return;
                if (v.Take(json, out problem))
                {
                    _copy.Accept(v.Key, json);
                    return;
                }
            }
            if (_once.Add("read:" + v.Key))
                Plugin.Log.LogWarning("Ectype: host room value '" + v.Key + "': the room's value does not read as " +
                                      v.TypeName + " within " + v.Cap + " bytes (" + problem + "); this client keeps " +
                                      "the value it had.");
        }

        private static void RaiseAll()
        {
            foreach (IHostRoomValue v in _values) v.RaiseIfChanged();
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
        /// <see cref="Value"/> changes at once; every value set in the same frame leaves in one
        /// room update. False, writing nothing, on a client, outside a room, for a value that was
        /// refused at registration, over <see cref="MaxBytes"/>, over the 16384-byte total of every
        /// host room value, or one that does not read back as <typeparamref name="T"/>. The value
        /// is copied: changing the object afterwards changes nothing.
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
