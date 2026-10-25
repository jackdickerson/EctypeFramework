using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using BepInEx;
using ExitGames.Client.Photon;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Ectype
{
    /// <summary>Per-key options for <see cref="SessionData.Register{T}"/>.</summary>
    public sealed class SessionDataOptions
    {
        /// <summary>Largest compact JSON size of this key's value, in UTF-8 bytes. Clamped to
        /// <see cref="SessionData.EnvelopeCapBytes"/>. A value over it is left out of the
        /// session with one error naming the key. Default
        /// <see cref="SessionData.DefaultKeyCapBytes"/>.</summary>
        public int MaxBytes = SessionData.DefaultKeyCapBytes;

        /// <summary>When a saved run is resumed, send the value the run started with (read from
        /// the host's sidecar) instead of producing a fresh one. Default true.</summary>
        public bool KeepOnResume = true;
    }

    /// <summary>
    /// Values the host decides for a whole session, delivered to every client inside the game's
    /// own session data.
    ///
    /// The host starts every session, the hub included, by serialising the session to JSON
    /// (<c>GameSession.AsJObject</c>) and raising it as the game's event 9, which is cached in the
    /// room for late joiners; every other client rebuilds the session from it
    /// (<c>GameSession.FromJObject</c>). The framework adds one key, <c>"ectype"</c>, to that JSON
    /// holding every registered value. The host produces each value once per session, at that
    /// serialisation; it cannot change until the host starts the next session.
    ///
    /// A saved run is rebuilt from fixed fields that drop the key, so the host also keeps the
    /// values in a sidecar file keyed by the session id and puts them back when it resumes that
    /// session.
    /// </summary>
    public static class SessionData
    {
        /// <summary>The single top-level key the framework adds to the session JSON.</summary>
        public const string EnvelopeKey = "ectype";

        /// <summary>Largest envelope the host sends or a client accepts, in UTF-8 bytes of
        /// compact JSON. Every joiner downloads it with the session.</summary>
        public const int EnvelopeCapBytes = 16384;

        /// <summary>The default per-key cap.</summary>
        public const int DefaultKeyCapBytes = 4096;

        /// <summary>Prefix of the room property a <see cref="HostValue{T}"/> publishes in the hub.</summary>
        public const string RoomPropertyPrefix = "Ectype.";

        internal const int FormatVersion = 1;
        internal const int SidecarMaxAgeDays = 30;
        internal const int SidecarMaxCount = 64;
        private const int MaxKeyLength = 128;
        private const float PollSeconds = 0.25f;

        // ------------------------------------------------------------------ registrations

        internal abstract class Entry
        {
            internal string Key;
            internal SessionDataOptions Options;
            internal abstract string TypeName { get; }
            internal abstract JToken Produce();
            internal abstract bool TryRead(JToken token, out object value, out string error);
            internal abstract void Apply(object value);
        }

        private sealed class Entry<T> : Entry
        {
            internal Func<T> HostProduce;
            internal Action<T> OnApply;

            internal override string TypeName { get { return typeof(T).Name; } }

            internal override JToken Produce()
            {
                T v = HostProduce();
                if (v == null) return null;
                return JToken.FromObject(v);
            }

            internal override bool TryRead(JToken token, out object value, out string error)
            {
                value = null;
                error = null;
                try
                {
                    value = token.ToObject<T>();
                    return true;
                }
                catch (Exception e)
                {
                    error = e.Message;
                    return false;
                }
            }

            internal override void Apply(object value)
            {
                if (OnApply != null) OnApply((T)value);
            }
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ the current session

        private sealed class SessionState
        {
            internal string Id;
            internal bool IsHub;
            /// <summary>The envelope's keys as received or sent, registered or not.</summary>
            internal JObject Keys;
            /// <summary>Decoded values of the registered keys that read cleanly.</summary>
            internal readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private static SessionState _current;

        /// <summary>The envelope attached to each session object this process has serialised or
        /// rebuilt, so a later serialisation of the same object writes the same values.</summary>
        private static readonly ConditionalWeakTable<GameSession, JObject> _attached =
            new ConditionalWeakTable<GameSession, JObject>();

        // event 9 sender, recorded around the game's handler
        private static bool _senderHook;
        private static bool _inEvent9;
        private static int _event9Sender;

        private static bool _patched;
        private static bool _warnedInert;
        private static float _nextPoll;
        private static LoadBalancingClient _subscribed;
        private static readonly HashSet<string> _once = new HashSet<string>(StringComparer.Ordinal);

        internal static readonly List<IHostValue> HostValues = new List<IHostValue>();

#if ECTYPE_DEV
        /// <summary>Dev self-test hook: while set, the FromJObject postfix hands the envelope token
        /// here and leaves the session state alone.</summary>
        internal static Action<JToken> SelfTestSink;
#endif

        /// <summary>
        /// Raised on every client with the key whose current value appeared, changed or went
        /// away: when a session's data arrives (the host's own included) and when the client leaves
        /// the room. Each subscriber runs in its own try/catch.
        /// </summary>
        public static event Action<string> Changed;

        /// <summary>The id of the session whose data is current on this client
        /// (<c>GameSession.GameSessionID</c>), or null outside a room or before any arrived.</summary>
        public static string SessionId { get { return _current != null ? _current.Id : null; } }

        /// <summary>
        /// Registers a session value. Call it at plugin <c>Awake</c>, on every client, with a key
        /// that is the same literal on every client; prefix it with your plugin GUID.
        ///
        /// On the host, <paramref name="hostProduce"/> runs once per session as the session is
        /// serialised; a null result leaves the key out. On every client, the host included,
        /// <paramref name="apply"/> runs with the host's value when a session carrying the key
        /// arrives, and never when the key is absent. A client that registers after a session
        /// arrived gets <paramref name="apply"/> at once when the current session carries the key.
        /// Returns false and logs the reason when the key is empty, too long, already registered,
        /// or the framework is inert.
        /// </summary>
        public static bool Register<T>(string key, Func<T> hostProduce, Action<T> apply,
                                       SessionDataOptions opts = null)
        {
            string problem = CheckKey(key);
            if (problem == null && hostProduce == null) problem = "no host producer";
            if (problem == null && _byKey.ContainsKey(key)) problem = "the key is already registered";
            if (problem != null)
            {
                Plugin.Log.LogError("Ectype: session data: Register('" + key + "') refused: " + problem + ".");
                return false;
            }
            if (!Plugin.PatchesApplied || !_patched)
            {
                if (!_warnedInert)
                {
                    _warnedInert = true;
                    Plugin.Log.LogWarning("Ectype: session data: '" + key + "' not registered: the session-data " +
                                          "patches are not applied this session, so no host value reaches this client.");
                }
                return false;
            }

            SessionDataOptions o = opts ?? new SessionDataOptions();
            Entry<T> entry = new Entry<T>
            {
                Key = key,
                Options = new SessionDataOptions
                {
                    MaxBytes = Math.Max(1, Math.Min(o.MaxBytes, EnvelopeCapBytes)),
                    KeepOnResume = o.KeepOnResume,
                },
                HostProduce = hostProduce,
                OnApply = apply,
            };
            _entries.Add(entry);
            _byKey[key] = entry;

            // A session that already arrived carries values for late registrations too.
            SessionState s = _current;
            JToken token;
            if (s != null && s.Keys != null && s.Keys.TryGetValue(key, out token))
            {
                object value;
                if (Decode(entry, token, out value))
                {
                    s.Values[key] = value;
                    SafeApply(entry, value);
                }
            }
            return true;
        }

        /// <summary>The host's value of <paramref name="key"/> for the current session, or
        /// <c>default(T)</c> when the session does not carry it or it does not read as
        /// <typeparamref name="T"/>.</summary>
        public static T Current<T>(string key)
        {
            T v;
            return TryGetCurrent(key, out v) ? v : default(T);
        }

        /// <summary>True when the current session carries <paramref name="key"/> and it reads as
        /// <typeparamref name="T"/>.</summary>
        public static bool TryGetCurrent<T>(string key, out T value)
        {
            value = default(T);
            SessionState s = _current;
            if (s == null || key == null) return false;
            object boxed;
            if (s.Values.TryGetValue(key, out boxed))
            {
                if (boxed is T)
                {
                    value = (T)boxed;
                    return true;
                }
                if (boxed == null) return false;
            }
            JToken token;
            if (s.Keys == null || !s.Keys.TryGetValue(key, out token)) return false;
            try
            {
                value = token.ToObject<T>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the current session carries <paramref name="key"/>.</summary>
        public static bool Has(string key)
        {
            SessionState s = _current;
            return s != null && s.Keys != null && key != null && s.Keys[key] != null;
        }

        /// <summary>
        /// A config entry whose value is the host's on every client: the host's own config on the
        /// host, the host's published value on a client, and this client's own config before any
        /// host value arrives (<see cref="HostValue{T}.IsFromHost"/> false). In the hub the host
        /// publishes it live as the room property <c>Ectype.&lt;key&gt;</c>; from the start of a
        /// mission every client, the host included, reads the value frozen into the session data.
        /// <paramref name="key"/> defaults to the config file name, section and key. Call at
        /// plugin <c>Awake</c> on every client. Never null.
        /// </summary>
        public static HostValue<T> HostValue<T>(BepInEx.Configuration.ConfigEntry<T> entry, string key = null)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (string.IsNullOrEmpty(key)) key = DefaultHostValueKey(entry);
            foreach (IHostValue existing in HostValues)
            {
                if (existing.Key != key) continue;
                HostValue<T> same = existing as HostValue<T>;
                if (same != null && ReferenceEquals(same.Entry, entry)) return same;
                Plugin.Log.LogError("Ectype: host value '" + key + "' is already bound to another config " +
                                    "entry; this one follows the local config only.");
                return new HostValue<T>(key, entry, false);
            }
            bool live = Register<T>(key, () => entry.Value, null);
            HostValue<T> hv = new HostValue<T>(key, entry, live);
            if (live) HostValues.Add(hv);
            hv.Recompute();
            return hv;
        }

        internal static string DefaultHostValueKey<T>(BepInEx.Configuration.ConfigEntry<T> entry)
        {
            string file = null;
            try { file = Path.GetFileNameWithoutExtension(entry.ConfigFile.ConfigFilePath); }
            catch { }
            return (string.IsNullOrEmpty(file) ? "config" : file) + "/" +
                   entry.Definition.Section + "." + entry.Definition.Key;
        }

        private static string CheckKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "the key is empty";
            if (key.Length > MaxKeyLength) return "the key is longer than " + MaxKeyLength + " characters";
            foreach (char c in key)
                if (char.IsControl(c)) return "the key contains a control character";
            return null;
        }

        /// <summary>True while the current session is a mission (not the hub).</summary>
        internal static bool InMission { get { return _current != null && !_current.IsHub; } }

#if ECTYPE_DEV
        /// <summary>Dev self-test: attaches an envelope to a session object, as the host's
        /// capture does.</summary>
        internal static void AttachForTest(GameSession session, JObject envelope)
        {
            _attached.Remove(session);
            _attached.Add(session, envelope);
        }
#endif

        internal static bool TryGetCurrentBoxed(string key, out object value)
        {
            value = null;
            SessionState s = _current;
            return s != null && s.Values.TryGetValue(key, out value);
        }

        // ------------------------------------------------------------------ patches

        internal static void ApplyPatches(Harmony harmony)
        {
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            MethodInfo asJ = typeof(GameSession).GetMethod("AsJObject", I, null, Type.EmptyTypes, null);
            MethodInfo fromJ = typeof(GameSession).GetMethod("FromJObject", S, null, new[] { typeof(JObject) }, null);
            if (asJ == null || fromJ == null)
            {
                Plugin.Log.LogError("Ectype: session data: GameSession.AsJObject / FromJObject not found " +
                                    "(game update?); session data and host values follow each client's own config.");
                return;
            }
            harmony.Patch(asJ, postfix: Hook("AsJObjectPostfix"));
            harmony.Patch(fromJ, postfix: Hook("FromJObjectPostfix"));
            _patched = true;

            MethodInfo onEvent = typeof(PhotonService).GetMethod("OnEvent", I, null, new[] { typeof(EventData) }, null);
            if (onEvent != null)
            {
                harmony.Patch(onEvent, prefix: Hook("OnEventPrefix"), finalizer: Hook("OnEventFinalizer"));
                _senderHook = true;
            }
            else
            {
                Plugin.Log.LogWarning("Ectype: session data: PhotonService.OnEvent(EventData) not found (game " +
                                      "update?); the session data's sender is not checked.");
            }

            MethodInfo save = typeof(GameSession).GetMethod("GetPreservedState", I, null, Type.EmptyTypes, null);
            if (save != null)
                harmony.Patch(save, postfix: Hook("GetPreservedStatePostfix"));
            else
                Plugin.Log.LogWarning("Ectype: session data: GameSession.GetPreservedState not found (game " +
                                      "update?); a saved run's sidecar is written only at mission start.");
            Plugin.Log.LogInfo("Ectype: session data: patched.");
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(SessionData).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>Host: produces the envelope the first time a session object is serialised and
        /// writes it into the JSON. Any client: writes an envelope already attached.</summary>
        private static void AsJObjectPostfix(GameSession __instance, JObject __result)
        {
            try
            {
                if (__instance == null || __result == null) return;
                JObject envelope;
                if (!_attached.TryGetValue(__instance, out envelope))
                {
                    if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient) return;
                    envelope = Capture(__instance);
                    _attached.Add(__instance, envelope);
                    SetCurrent(__instance.GameSessionID, __instance.IsHub, Keys(envelope),
                               "sent with session", PhotonNetwork.LocalPlayer.ActorNumber);
                    if (!__instance.IsHub && Keys(envelope).Count > 0)
                        SaveSidecar(__instance.GameSessionID, Keys(envelope));
                }
                if (envelope != null && Keys(envelope).Count > 0) __result[EnvelopeKey] = envelope.DeepClone();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: adding the envelope to the session JSON failed: " + e);
            }
        }

        /// <summary>Every client but the host: reads the envelope from the session the host sent.</summary>
        private static void FromJObjectPostfix(JObject __0, GameSession __result)
        {
            try
            {
#if ECTYPE_DEV
                if (SelfTestSink != null)
                {
                    SelfTestSink(__0 != null ? __0[EnvelopeKey] : null);
                    return;
                }
#endif
                if (__result == null) return;
                // Only the game's event-9 handler is the session arriving; another caller is
                // parsing a session for its own purposes.
                if (_senderHook && !_inEvent9) return;

                int master = MasterActor();
                int sender = _senderHook ? _event9Sender : -1;
                string why = SenderProblem(sender, master);
                if (why != null)
                {
                    Plugin.Log.LogWarning("Ectype: session data: ignored the session data from actor " + sender +
                                          ", " + why + " (master is actor " + master + ").");
                    SetCurrent(__result.GameSessionID, __result.IsHub, new JObject(), null, sender);
                    return;
                }

                string problem;
                JObject keys = Unpack(__0 != null ? __0[EnvelopeKey] : null, out problem);
                if (problem != null)
                {
                    Plugin.Log.LogWarning("Ectype: session data: ignored the host's envelope: " + problem + ".");
                    keys = null;
                }
                if (keys == null) keys = new JObject();
                _attached.Remove(__result);
                _attached.Add(__result, Wrap(keys));
                SetCurrent(__result.GameSessionID, __result.IsHub, keys, "received from the host", sender);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: reading the envelope from the session JSON failed: " + e);
            }
        }

        private static void OnEventPrefix(EventData __0)
        {
            try
            {
                if (__0 == null || __0.Code != 9) return;
                _inEvent9 = true;
                _event9Sender = __0.Sender;
            }
            catch { }
        }

        private static Exception OnEventFinalizer(Exception __exception)
        {
            _inEvent9 = false;
            return __exception;
        }

        /// <summary>Host: the run is being saved; refresh its sidecar with the values it carries.</summary>
        private static void GetPreservedStatePostfix(PreservedGameSession __result)
        {
            try
            {
                SessionState s = _current;
                if (__result == null || s == null || s.IsHub || s.Keys == null || s.Keys.Count == 0) return;
                if (!string.Equals(s.Id, __result.GameSessionID, StringComparison.Ordinal)) return;
                SaveSidecar(s.Id, s.Keys);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: refreshing the saved-run sidecar failed: " + e);
            }
        }

        /// <summary>Null when the sender may have written the session data. The game hands the
        /// master role to another player only when the master leaves, so a sender who is still in
        /// the room and is not the master raised it without being the host.</summary>
        internal static string SenderProblem(int sender, int master)
        {
            if (sender <= 0 || sender == master) return null;
            Room room = PhotonNetwork.CurrentRoom;
            if (room == null || room.GetPlayer(sender) == null) return null;
            return "who is in the room and is not the master client";
        }

        private static int MasterActor()
        {
            Photon.Realtime.Player m = PhotonNetwork.MasterClient;
            return m != null ? m.ActorNumber : -1;
        }

        // ------------------------------------------------------------------ host capture

        private static JObject Capture(GameSession session)
        {
            string id = session.GameSessionID;
            bool resumed = false;
            JObject saved = null;
            if (!session.IsHub && ValidSessionId(id))
            {
                string error;
                saved = ReadSidecar(SidecarDir, id, out error);
                if (error != null)
                    Plugin.Log.LogWarning("Ectype: session data: saved-run sidecar for session " + Short(id) +
                                          " unreadable; values are produced fresh: " + error);
                resumed = saved != null;
            }

            List<PackItem> items = new List<PackItem>();
            int restored = 0;
            foreach (Entry e in _entries)
            {
                JToken token = null;
                JToken old;
                object probe;
                string readError;
                if (saved != null && e.Options.KeepOnResume && saved.TryGetValue(e.Key, out old) &&
                    e.TryRead(old, out probe, out readError))
                {
                    token = old;
                    restored++;
                }
                else
                {
                    try { token = e.Produce(); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("Ectype: session data: producing key '" + e.Key + "' threw; left out " +
                                            "of this session: " + ex);
                        continue;
                    }
                }
                if (token != null) items.Add(new PackItem { Key = e.Key, Token = token, Cap = e.Options.MaxBytes });
            }

            List<string> refusals = new List<string>();
            JObject envelope = Pack(items, refusals);
            foreach (string r in refusals)
                Plugin.Log.LogError("Ectype: session data: " + r + "; left out of this session.");
            int count = Keys(envelope).Count;
            if (count > 0)
                Plugin.Log.LogInfo("Ectype: session data: sending " + count + " key(s) with " +
                                   (session.IsHub ? "hub" : "mission") + " session " + Short(id) + " (" +
                                   Bytes(envelope) + " bytes" +
                                   (resumed ? ", " + restored + " restored from the saved run" : "") + ").");
            return envelope;
        }

        // ------------------------------------------------------------------ current state

        private static void SetCurrent(string id, bool isHub, JObject keys, string verb, int from)
        {
            SessionState prev = _current;
            SessionState next = null;
            if (keys != null)
            {
                next = new SessionState { Id = id, IsHub = isHub, Keys = keys };
                foreach (Entry e in _entries)
                {
                    JToken token;
                    object value;
                    if (keys.TryGetValue(e.Key, out token) && Decode(e, token, out value))
                        next.Values[e.Key] = value;
                }
            }
            _current = next;

            if (next != null && verb != null && next.Keys.Count > 0)
            {
                if (verb == "received from the host")
                    Plugin.Log.LogInfo("Ectype: session data: received " + next.Keys.Count + " key(s) from the host " +
                                       "(actor " + from + ") with " + (isHub ? "hub" : "mission") + " session " +
                                       Short(id) + ".");
                else if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: session data: applied " + next.Keys.Count + " key(s) locally.");
            }

            if (next != null)
                foreach (Entry e in _entries)
                {
                    object value;
                    if (next.Values.TryGetValue(e.Key, out value)) SafeApply(e, value);
                }

            RaiseChanged(prev, next);
            foreach (IHostValue hv in HostValues) hv.Recompute();
        }

        private static bool Decode(Entry e, JToken token, out object value)
        {
            string error;
            if (token != null && Bytes(token) <= e.Options.MaxBytes && e.TryRead(token, out value, out error))
                return true;
            value = null;
            if (_once.Add("read:" + e.Key))
                Plugin.Log.LogWarning("Ectype: session data: key '" + e.Key + "' from the host does not read as " +
                                      e.TypeName + " within " + e.Options.MaxBytes + " bytes; this client keeps no " +
                                      "value for it.");
            return false;
        }

        private static void SafeApply(Entry e, object value)
        {
            try { e.Apply(value); }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Ectype: session data: applying key '" + e.Key + "' threw: " + ex);
            }
        }

        private static void RaiseChanged(SessionState prev, SessionState next)
        {
            Action<string> handlers = Changed;
            if (handlers == null) return;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            if (prev != null && prev.Keys != null) foreach (KeyValuePair<string, JToken> kv in prev.Keys) keys.Add(kv.Key);
            if (next != null && next.Keys != null) foreach (KeyValuePair<string, JToken> kv in next.Keys) keys.Add(kv.Key);
            foreach (string key in keys)
            {
                JToken a = prev != null && prev.Keys != null ? prev.Keys[key] : null;
                JToken b = next != null && next.Keys != null ? next.Keys[key] : null;
                if (JToken.DeepEquals(a, b)) continue;
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    try { ((Action<string>)d)(key); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("Ectype: SessionData.Changed subscriber " + d.Method.DeclaringType + "." +
                                            d.Method.Name + " threw: " + ex);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ driver

        internal static void Poll()
        {
            if (!_patched && HostValues.Count == 0) return;

            LoadBalancingClient client = PhotonNetwork.NetworkingClient;
            if (client != _subscribed)
            {
                if (_subscribed != null) _subscribed.EventReceived -= OnPhotonEvent;
                if (client != null) client.EventReceived += OnPhotonEvent;
                _subscribed = client;
            }

            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            if (!PhotonNetwork.InRoom)
            {
                if (_current != null) SetCurrent(null, false, null, null, 0);
                foreach (IHostValue hv in HostValues) hv.LeftRoom();
                return;
            }
            foreach (IHostValue hv in HostValues)
            {
                try { hv.Tick(Time.unscaledTime); }
                catch (Exception e)
                {
                    if (_once.Add("tick:" + hv.Key))
                        Plugin.Log.LogError("Ectype: host value '" + hv.Key + "' update failed (reported once): " + e);
                }
            }
        }

        /// <summary>Records who wrote each host-value room property. Photon raises event 253 for a
        /// property change with the writer as the event's sender, after it has merged the change
        /// into the room's copy.</summary>
        private static void OnPhotonEvent(EventData ev)
        {
            try
            {
                if (ev == null || ev.Code != 253 || HostValues.Count == 0) return;
                if (ev.Parameters.ContainsKey(253) && ev[253] is int && (int)ev[253] != 0) return;
                ExitGames.Client.Photon.Hashtable props = ev[251] as ExitGames.Client.Photon.Hashtable;
                if (props == null) return;
                int sender = ev.Sender;
                foreach (IHostValue hv in HostValues)
                {
                    object raw;
                    if (props.TryGetValue(hv.PropertyName, out raw)) hv.Written(sender, raw as string);
                }
            }
            catch (Exception e)
            {
                if (_once.Add("253"))
                    Plugin.Log.LogError("Ectype: host value writer check failed (reported once): " + e);
            }
        }

        // ------------------------------------------------------------------ envelope (pure)

        internal sealed class PackItem
        {
            internal string Key;
            internal JToken Token;
            internal int Cap;
        }

        /// <summary>Builds <c>{"v":1,"keys":{...}}</c> in item order, leaving out each item over
        /// its own cap or that would bring the envelope over <see cref="EnvelopeCapBytes"/>. Each
        /// refusal is described in <paramref name="refusals"/>, naming its key.</summary>
        internal static JObject Pack(IEnumerable<PackItem> items, List<string> refusals)
        {
            JObject keys = new JObject();
            JObject envelope = Wrap(keys);
            foreach (PackItem item in items)
            {
                if (item == null || item.Key == null || item.Token == null) continue;
                int size = Bytes(item.Token);
                int cap = Math.Min(item.Cap, EnvelopeCapBytes);
                if (size > cap)
                {
                    refusals.Add("key '" + item.Key + "' is " + size + " bytes, over its " + cap + "-byte cap");
                    continue;
                }
                keys[item.Key] = item.Token;
                int total = Bytes(envelope);
                if (total > EnvelopeCapBytes)
                {
                    keys.Remove(item.Key);
                    refusals.Add("key '" + item.Key + "' would bring the envelope to " + total + " bytes, over the " +
                                 EnvelopeCapBytes + "-byte cap");
                }
            }
            return envelope;
        }

        /// <summary>The keys object of an envelope, or null when the token is absent. A problem
        /// (not an object, over the cap, another format) is described and null returned.</summary>
        internal static JObject Unpack(JToken envelope, out string problem)
        {
            problem = null;
            if (envelope == null || envelope.Type == JTokenType.Null) return null;
            JObject o = envelope as JObject;
            if (o == null)
            {
                problem = "it is not a JSON object";
                return null;
            }
            int size = Bytes(o);
            if (size > EnvelopeCapBytes)
            {
                problem = "it is " + size + " bytes, over the " + EnvelopeCapBytes + "-byte cap";
                return null;
            }
            JToken v = o["v"];
            if (v == null || v.Type != JTokenType.Integer || v.Value<int>() != FormatVersion)
            {
                problem = "its format is " + (v != null ? v.ToString(Formatting.None) : "missing") +
                          ", this framework reads " + FormatVersion;
                return null;
            }
            JObject keys = o["keys"] as JObject;
            if (keys == null)
            {
                problem = "it has no keys object";
                return null;
            }
            return keys;
        }

        internal static JObject Wrap(JObject keys)
        {
            return new JObject { ["v"] = FormatVersion, ["keys"] = keys };
        }

        private static JObject Keys(JObject envelope)
        {
            return envelope != null ? envelope["keys"] as JObject ?? new JObject() : new JObject();
        }

        internal static int Bytes(JToken token)
        {
            return token == null ? 0 : Encoding.UTF8.GetByteCount(token.ToString(Formatting.None));
        }

        internal static int Bytes(string s)
        {
            return s == null ? 0 : Encoding.UTF8.GetByteCount(s);
        }

        private static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "(no id)" : id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // ------------------------------------------------------------------ host-value room format (pure)

        /// <summary>The room property value: <c>{"a":&lt;master actor&gt;,"v":&lt;value&gt;}</c>.</summary>
        internal static string PackRoomValue(int actor, JToken value)
        {
            return new JObject { ["a"] = actor, ["v"] = value }.ToString(Formatting.None);
        }

        /// <summary>The value of a host-value room property, or null with a problem described. The
        /// property must be a string within <paramref name="cap"/> bytes holding a JSON object
        /// stamped with <paramref name="master"/>'s actor number.</summary>
        internal static JToken UnpackRoomValue(object raw, int master, int cap, out string problem)
        {
            problem = null;
            string s = raw as string;
            if (s == null)
            {
                problem = raw == null ? "absent" : "not a string";
                return null;
            }
            if (Bytes(s) > cap)
            {
                problem = Bytes(s) + " bytes, over the " + cap + "-byte cap";
                return null;
            }
            JObject o;
            try { o = JObject.Parse(s); }
            catch (Exception e)
            {
                problem = "not a JSON object (" + e.Message + ")";
                return null;
            }
            JToken a = o["a"];
            if (a == null || a.Type != JTokenType.Integer)
            {
                problem = "no actor stamp";
                return null;
            }
            if (a.Value<int>() != master)
            {
                problem = "stamped by actor " + a.Value<int>() + ", not the master client " + master;
                return null;
            }
            JToken v = o["v"];
            if (v == null)
            {
                problem = "no value";
                return null;
            }
            return v;
        }

        // ------------------------------------------------------------------ sidecar

        /// <summary><c>BepInEx/config/&lt;framework guid&gt;.sessions</c>: one file per saved
        /// session, named by its id.</summary>
        internal static string SidecarDir
        {
            get { return Path.Combine(Paths.ConfigPath, EctypePluginInfo.PLUGIN_GUID + ".sessions"); }
        }

        /// <summary>The game's session ids are <c>Guid.ToString()</c>; anything else never names a file.</summary>
        internal static bool ValidSessionId(string id)
        {
            Guid g;
            return !string.IsNullOrEmpty(id) && Guid.TryParseExact(id, "D", out g);
        }

        private static void SaveSidecar(string id, JObject keys)
        {
            if (!ValidSessionId(id)) return;
            string dir = SidecarDir;
            string error;
            if (!WriteSidecar(dir, id, keys, out error))
            {
                Plugin.Log.LogWarning("Ectype: session data: saved-run sidecar write failed (" +
                                      Path.Combine(dir, id + ".json") + "): " + error);
                return;
            }
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: session data: saved-run sidecar written for session " + Short(id) + ".");
            try
            {
                int removed = PruneSidecars(dir, TimeSpan.FromDays(SidecarMaxAgeDays), SidecarMaxCount, DateTime.UtcNow);
                if (removed > 0 && Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: session data: pruned " + removed + " old saved-run sidecar(s).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: session data: pruning saved-run sidecars failed: " + e.Message);
            }
        }

        internal static bool WriteSidecar(string dir, string id, JObject keys, out string error)
        {
            error = null;
            if (!ValidSessionId(id))
            {
                error = "not a session id";
                return false;
            }
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, id + ".json");
                string tmp = path + ".tmp";
                JObject doc = new JObject { ["v"] = FormatVersion, ["session"] = id, ["keys"] = keys };
                File.WriteAllText(tmp, doc.ToString(Formatting.None), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>The saved keys for a session, or null when it has no sidecar. A file that
        /// exists and does not read is described in <paramref name="error"/>.</summary>
        internal static JObject ReadSidecar(string dir, string id, out string error)
        {
            error = null;
            if (!ValidSessionId(id)) return null;
            string path = Path.Combine(dir, id + ".json");
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (Bytes(text) > EnvelopeCapBytes * 2)
                {
                    error = "the file is larger than any envelope";
                    return null;
                }
                JObject doc = JObject.Parse(text);
                JToken v = doc["v"];
                if (v == null || v.Type != JTokenType.Integer || v.Value<int>() != FormatVersion ||
                    (string)doc["session"] != id || !(doc["keys"] is JObject))
                {
                    error = "unexpected layout";
                    return null;
                }
                return (JObject)doc["keys"];
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>Deletes sidecars last written more than <paramref name="maxAge"/> ago, then
        /// all but the newest <paramref name="maxCount"/>. Only files named by a session id are
        /// touched. Returns how many were deleted.</summary>
        internal static int PruneSidecars(string dir, TimeSpan maxAge, int maxCount, DateTime nowUtc)
        {
            if (!Directory.Exists(dir)) return 0;
            List<FileInfo> files = new List<FileInfo>();
            foreach (string path in Directory.GetFiles(dir, "*.json"))
                if (ValidSessionId(Path.GetFileNameWithoutExtension(path))) files.Add(new FileInfo(path));
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            int removed = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (i < maxCount && nowUtc - files[i].LastWriteTimeUtc <= maxAge) continue;
                try
                {
                    files[i].Delete();
                    removed++;
                }
                catch { }
            }
            return removed;
        }
    }

    /// <summary>Where a <see cref="HostValue{T}"/>'s current value came from.</summary>
    public enum HostValueSource
    {
        /// <summary>This client's own config entry: on the host, the host's value; on a client,
        /// no host value has arrived.</summary>
        LocalConfig,
        /// <summary>The room property the host publishes in the hub.</summary>
        Room,
        /// <summary>The session data frozen when the host started the current session.</summary>
        Session,
    }

    internal interface IHostValue
    {
        string Key { get; }
        string PropertyName { get; }
        void Recompute();
        void Tick(float now);
        void Written(int sender, string raw);
        void LeftRoom();
    }

    /// <summary>
    /// A config entry read as the host's value. Get one from
    /// <see cref="SessionData.HostValue{T}"/>. Read <see cref="Value"/> wherever the mod would read
    /// the entry's own value for a decision the host owns.
    /// </summary>
    public sealed class HostValue<T> : IHostValue
    {
        private const float RepublishSeconds = 2f;

        private readonly bool _live;
        private T _value;
        private bool _fromHost;
        private HostValueSource _source;
        private bool _computed;

        // room property, client side
        private string _roomRaw;
        private int _roomMaster = int.MinValue;
        private bool _roomHas;
        private T _roomValue;
        // last writer Photon reported for the property, with the value it wrote
        private int _writer;
        private string _writerRaw;
        private readonly HashSet<int> _warnedWriters = new HashSet<int>();
        private bool _warnedStamp, _warnedSize;

        // room property, host side
        private string _published;
        private float _nextPublish;

        internal HostValue(string key, BepInEx.Configuration.ConfigEntry<T> entry, bool live)
        {
            Key = key;
            Entry = entry;
            _live = live;
            entry.SettingChanged += (s, e) => { if (_live) _nextPublish = 0f; Recompute(); };
        }

        /// <summary>The key, in the session data and in the room property <c>Ectype.&lt;key&gt;</c>.</summary>
        public string Key { get; private set; }

        /// <summary>This client's own config entry.</summary>
        public BepInEx.Configuration.ConfigEntry<T> Entry { get; private set; }

        /// <summary>The host's value; see <see cref="SessionData.HostValue{T}"/>.</summary>
        public T Value
        {
            get
            {
                if (!_computed) Recompute();
                return _value;
            }
        }

        /// <summary>True when <see cref="Value"/> is the host's: always on the host, and on a client
        /// once a host value has arrived.</summary>
        public bool IsFromHost
        {
            get
            {
                if (!_computed) Recompute();
                return _fromHost;
            }
        }

        /// <summary>Where <see cref="Value"/> came from.</summary>
        public HostValueSource Source
        {
            get
            {
                if (!_computed) Recompute();
                return _source;
            }
        }

        /// <summary>Raised with the new value when <see cref="Value"/> changes. Each subscriber runs
        /// in its own try/catch.</summary>
        public event Action<T> Changed;

        string IHostValue.PropertyName { get { return SessionData.RoomPropertyPrefix + Key; } }

        void IHostValue.Recompute() { Recompute(); }

        /// <summary>Applies the precedence in force: in a mission, the session's frozen value; in
        /// the hub, the host's own config on the host, else the host's room property, else the hub
        /// session's value; this client's config when none of those exists.</summary>
        internal void Recompute()
        {
            T local = Entry.Value;
            T value = local;
            HostValueSource source = HostValueSource.LocalConfig;
            bool fromHost = false;

            if (_live && PhotonNetwork.InRoom)
            {
                bool host = PhotonNetwork.IsMasterClient;
                object boxed;
                bool hasSession = SessionData.TryGetCurrentBoxed(Key, out boxed) && boxed is T;
                if (SessionData.InMission)
                {
                    if (hasSession)
                    {
                        value = (T)boxed;
                        source = HostValueSource.Session;
                        fromHost = true;
                    }
                    else fromHost = host;
                }
                else if (host) fromHost = true;
                else if (_roomHas)
                {
                    value = _roomValue;
                    source = HostValueSource.Room;
                    fromHost = true;
                }
                else if (hasSession)
                {
                    value = (T)boxed;
                    source = HostValueSource.Session;
                    fromHost = true;
                }
            }

            bool changed = _computed && !EqualityComparer<T>.Default.Equals(_value, value);
            _value = value;
            _source = source;
            _fromHost = fromHost;
            _computed = true;
            if (changed) Raise(value);
        }

        private void Raise(T value)
        {
            Action<T> handlers = Changed;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<T>)d)(value); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: host value '" + Key + "' Changed subscriber " +
                                        d.Method.DeclaringType + "." + d.Method.Name + " threw: " + e);
                }
            }
        }

        void IHostValue.Tick(float now)
        {
            Room room = PhotonNetwork.CurrentRoom;
            if (room == null) return;
            string prop = SessionData.RoomPropertyPrefix + Key;
            object raw;
            room.CustomProperties.TryGetValue(prop, out raw);
            int master = room.MasterClientId;

            if (PhotonNetwork.IsMasterClient)
            {
                if (!SessionData.InMission) Publish(room, prop, raw as string, now);
            }
            else ReadRoom(raw, master);
            Recompute();
        }

        /// <summary>Host in the hub: keeps the room property equal to its own config, which also
        /// overwrites a value another player wrote.</summary>
        private void Publish(Room room, string prop, string current, float now)
        {
            JToken token = Entry.Value != null ? JToken.FromObject(Entry.Value) : JValue.CreateNull();
            string desired = SessionData.PackRoomValue(PhotonNetwork.LocalPlayer.ActorNumber, token);
            if (SessionData.Bytes(desired) > SessionData.DefaultKeyCapBytes)
            {
                if (!_warnedSize)
                {
                    _warnedSize = true;
                    Plugin.Log.LogError("Ectype: host value '" + Key + "' is over the " + SessionData.DefaultKeyCapBytes +
                                        "-byte cap; not published in the hub.");
                }
                return;
            }
            if (current == desired) return;
            if (desired == _published && now < _nextPublish) return;
            room.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { prop, desired } });
            _published = desired;
            _nextPublish = now + RepublishSeconds;
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: host value '" + Key + "' published to the room" +
                                   (current != null ? " (the room held another value)" : "") + ".");
        }

        /// <summary>Client in the hub: accepts the room property when its stamp names the current
        /// master and, if Photon reported its writer, the writer is the master.</summary>
        private void ReadRoom(object raw, int master)
        {
            string s = raw as string;
            if (s == _roomRaw && master == _roomMaster) return;

            if (s != null && s == _writerRaw && _writer > 0 && _writer != master)
            {
                if (_warnedWriters.Add(_writer))
                    Plugin.Log.LogWarning("Ectype: host value '" + Key + "': ignored a room-property write from actor " +
                                          _writer + ", who is not the master client (" + master + ").");
                _roomRaw = s;
                _roomMaster = master;
                return; // keep the last accepted value
            }

            _roomRaw = s;
            _roomMaster = master;
            string problem;
            JToken token = SessionData.UnpackRoomValue(raw, master, SessionData.DefaultKeyCapBytes, out problem);
            if (token == null)
            {
                _roomHas = false;
                if (raw != null && !_warnedStamp && Plugin.Verbose)
                {
                    _warnedStamp = true;
                    Plugin.Log.LogInfo("Ectype: host value '" + Key + "': room property not used (" + problem + ").");
                }
                return;
            }
            try
            {
                _roomValue = token.ToObject<T>();
                _roomHas = true;
            }
            catch (Exception e)
            {
                _roomHas = false;
                if (!_warnedStamp)
                {
                    _warnedStamp = true;
                    Plugin.Log.LogWarning("Ectype: host value '" + Key + "': the host's room value does not read as " +
                                          typeof(T).Name + ": " + e.Message);
                }
            }
        }

        void IHostValue.Written(int sender, string raw)
        {
            _writer = sender;
            _writerRaw = raw;
            _roomRaw = null; // decide again at the next tick
            if (PhotonNetwork.IsMasterClient && sender > 0 && sender != PhotonNetwork.LocalPlayer.ActorNumber)
            {
                _nextPublish = 0f; // repair at the next tick
                if (_warnedWriters.Add(sender))
                    Plugin.Log.LogWarning("Ectype: host value '" + Key + "': actor " + sender + " wrote the room " +
                                          "property; republishing the host's value.");
            }
        }

        void IHostValue.LeftRoom()
        {
            _roomRaw = null;
            _roomMaster = int.MinValue;
            _roomHas = false;
            _writer = 0;
            _writerRaw = null;
            _published = null;
            _nextPublish = 0f;
            _warnedWriters.Clear();
            _warnedStamp = false;
            Recompute();
        }
    }
}
