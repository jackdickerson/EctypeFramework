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
    /// The host starts every session, the hub included, by serialising it to JSON
    /// (<c>GameSession.AsJObject</c>) and raising it as the game's event 9, which is cached in the
    /// room for late joiners; every other client rebuilds the session from it
    /// (<c>GameSession.FromJObject</c>). The framework adds one key, <c>"ectype"</c>, holding
    /// every registered value. The host produces the values as its new session object becomes
    /// active (<c>GameSessionManager.PrepareNextSession</c>), and every machine applies them at
    /// that same point of its own start sequence, before the quest is generated.
    ///
    /// A saved run is rebuilt from fixed fields that drop the key, so the host also keeps the
    /// values in a sidecar file keyed by the id the save carries, and sends them again when it
    /// resumes that run.
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

        /// <summary>Prefix of the framework's room properties.</summary>
        public const string RoomPropertyPrefix = "Ectype.";

        /// <summary>The one room property in which the host publishes every
        /// <see cref="HostValue{T}"/> while in the hub.</summary>
        public const string HostValuesRoomProperty = RoomPropertyPrefix + "HostValues";

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

        // ------------------------------------------------------------------ session state

        /// <summary>The values that travel with one session object: produced by the host, or
        /// read from the JSON the host sent.</summary>
        private sealed class Attached
        {
            internal JObject Keys;
            /// <summary>The session id the host sent. A client's own session object gets a new
            /// random id when it activates; this keeps the host's.</summary>
            internal string HostId;
            internal bool IsHub;
            /// <summary>The actor number of the host that produced the values.</summary>
            internal int Actor;
        }

        private sealed class SessionState
        {
            internal GameSession Session;
            internal string Id;
            internal bool IsHub;
            /// <summary>The envelope's keys, registered or not.</summary>
            internal JObject Keys;
            /// <summary>Decoded values of the registered keys that read cleanly.</summary>
            internal readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private static SessionState _current;

        private static readonly ConditionalWeakTable<GameSession, Attached> _attached =
            new ConditionalWeakTable<GameSession, Attached>();

        // event 9 sender, recorded around the game's handler
        private static bool _senderHook;
        private static bool _inEvent9;
        private static int _event9Sender;

        private static bool _patched;
        private static bool _warnedInert;
        private static bool _hadRoomState;
        private static float _nextPoll;
        private static LoadBalancingClient _subscribed;
        private static readonly HashSet<string> _once = new HashSet<string>(StringComparer.Ordinal);

        internal static readonly List<IHostValue> HostValues = new List<IHostValue>();

#if ECTYPE_DEV
        /// <summary>Dev self-test hook: while set, the FromJObject postfix hands the envelope token
        /// here and attaches nothing.</summary>
        internal static Action<JToken> SelfTestSink;
#endif

        /// <summary>
        /// Raised on every client with the key whose current value appeared, changed or went
        /// away: when a new session becomes active (the host's own included) and when the client
        /// leaves the room. Each subscriber runs in its own try/catch.
        /// </summary>
        public static event Action<string> Changed;

        /// <summary>The id the host gave the session whose data is current, or null outside a room
        /// or before any session. On a client this differs from
        /// <c>GameSessionManager.ActiveSession.GameSessionID</c>, which the game replaces with a
        /// random id when the client activates the session.</summary>
        public static string SessionId { get { return _current != null ? _current.Id : null; } }

        /// <summary>
        /// Registers a session value. Call it at plugin <c>Awake</c>, on every client, with a key
        /// that is the same literal on every client; prefix it with your plugin GUID.
        ///
        /// On the host, <paramref name="hostProduce"/> runs once per session, as the new session
        /// object becomes active, after the previous scene has been unloaded and before the quest
        /// is generated; <c>GameSessionManager.ActiveSession</c> is then the new session. A null
        /// result leaves the key out. On every client, the host included,
        /// <paramref name="apply"/> runs with the host's value at the same point of that client's
        /// start sequence, and never for a session without the key. A client that registers after
        /// a session became active gets <paramref name="apply"/> at once if it carries the key.
        /// <typeparamref name="T"/> must be plain JSON data: numbers, bools, strings, enums, and
        /// arrays, lists or classes of them; Unity types and objects are refused.
        /// Returns false and logs the reason when the key or type is refused, the key is already
        /// registered, or the framework is inert.
        /// </summary>
        public static bool Register<T>(string key, Func<T> hostProduce, Action<T> apply,
                                       SessionDataOptions opts = null)
        {
            string problem = CheckKey(key);
            if (problem == null && hostProduce == null) problem = "no host producer";
            if (problem == null && _byKey.ContainsKey(key)) problem = "the key is already registered";
            if (problem == null) problem = TypeProblem(typeof(T), false);
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

            // A session already active carries values for late registrations too.
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
        /// publishes it live in the room property <see cref="HostValuesRoomProperty"/>; from the
        /// start of a mission every client, the host included, reads the value frozen into the
        /// session data. <typeparamref name="T"/> must be a bool, a number, an enum or a string.
        /// <paramref name="key"/> defaults to the config file name, section and key. Call at
        /// plugin <c>Awake</c> on every client. Never null; a refused entry follows the local
        /// config only.
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
            string typeProblem = TypeProblem(typeof(T), true);
            if (typeProblem != null)
            {
                Plugin.Log.LogError("Ectype: host value '" + key + "' refused: " + typeProblem +
                                    "; it follows the local config only.");
                return new HostValue<T>(key, entry, false);
            }
            bool live = Register<T>(key, () => entry.Value, null);
            HostValue<T> hv = new HostValue<T>(key, entry, live);
            if (live)
            {
                HostValues.Add(hv);
                HostValueRoom.MarkDirty();
            }
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

        /// <summary>Null when values of <paramref name="t"/> survive the game's JSON serializer.
        /// <paramref name="scalarOnly"/> accepts bools, numbers, enums and strings alone. Otherwise
        /// Unity types, delegates and abstract types are refused, and a default value is
        /// round-tripped when one can be built.</summary>
        internal static string TypeProblem(Type t, bool scalarOnly)
        {
            Type under = Nullable.GetUnderlyingType(t);
            if (under != null) t = under;
            if (t == typeof(string) || t == typeof(decimal) || t.IsEnum ||
                (t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr)))
                return null;
            if (scalarOnly) return t.Name + " is not a bool, number, enum or string";
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return t.Name + " is a Unity object";
            if (typeof(Delegate).IsAssignableFrom(t)) return t.Name + " is a delegate";
            if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
                return t.Name + " is a Unity type, which the game's JSON serializer has no converter for";
            if (t.IsInterface || t.IsAbstract || t.IsPointer) return t.Name + " cannot be read back from JSON";

            object sample = null;
            try
            {
                if (t.IsArray) sample = Array.CreateInstance(t.GetElementType(), 0);
                else if (t.IsValueType) sample = Activator.CreateInstance(t);
                else if (t.GetConstructor(Type.EmptyTypes) != null) sample = Activator.CreateInstance(t);
            }
            catch { sample = null; }
            if (sample == null) return null;
            try
            {
                JToken.FromObject(sample).ToObject(t);
                return null;
            }
            catch (Exception e)
            {
                return "a default " + t.Name + " does not survive JSON (" + e.Message + ")";
            }
        }

        /// <summary>True while the current session is a mission (not the hub).</summary>
        internal static bool InMission { get { return _current != null && !_current.IsHub; } }

#if ECTYPE_DEV
        /// <summary>Dev self-test: attaches values to a session object, as the host's capture
        /// does.</summary>
        internal static void AttachForTest(GameSession session, JObject keys, int actor)
        {
            Attach(session, new Attached { Keys = keys, HostId = session.GameSessionID, IsHub = session.IsHub, Actor = actor });
        }
#endif

        internal static bool TryGetCurrentBoxed(string key, out object value)
        {
            value = null;
            SessionState s = _current;
            return s != null && s.Values.TryGetValue(key, out value);
        }

        private static void Attach(GameSession session, Attached a)
        {
            _attached.Remove(session);
            _attached.Add(session, a);
        }

        // ------------------------------------------------------------------ patches

        internal static void ApplyPatches(Harmony harmony)
        {
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            MethodInfo asJ = typeof(GameSession).GetMethod("AsJObject", I, null, Type.EmptyTypes, null);
            MethodInfo fromJ = typeof(GameSession).GetMethod("FromJObject", S, null, new[] { typeof(JObject) }, null);
            MethodInfo prepare = typeof(GameSessionManager).GetMethod("PrepareNextSession", I, null,
                                                                      new[] { typeof(bool) }, null);
            if (asJ == null || fromJ == null || prepare == null)
            {
                Plugin.Log.LogError("Ectype: session data: GameSession.AsJObject / FromJObject or " +
                                    "GameSessionManager.PrepareNextSession not found (game update?); session data " +
                                    "and host values follow each client's own config.");
                return;
            }
            harmony.Patch(asJ, postfix: Hook("AsJObjectPostfix"));
            harmony.Patch(fromJ, postfix: Hook("FromJObjectPostfix"));
            harmony.Patch(prepare, postfix: Hook("PrepareNextSessionPostfix"));
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

        /// <summary>Every machine, as a new session object becomes active. The host captures the
        /// values; every machine makes them current and applies them. Runs before the quest is
        /// generated and before the first sector, the ship or any networked object exists.</summary>
        private static void PrepareNextSessionPostfix(bool __0)
        {
            try
            {
                GameSession session = GameSessionManager.ActiveSession;
                if (session == null) return;
                if (_current != null && ReferenceEquals(_current.Session, session)) return;

                Attached a;
                if (!_attached.TryGetValue(session, out a))
                {
                    if (__0 && PhotonNetwork.InRoom)
                    {
                        a = Capture(session);
                        Attach(session, a);
                        if (!session.IsHub && a.Keys.Count > 0) SaveSidecar(session.GameSessionID, a.Keys);
                    }
                    else
                    {
                        a = new Attached { Keys = new JObject(), HostId = session.GameSessionID, IsHub = session.IsHub };
                    }
                }
                SetCurrent(session, a);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: activating the session's values failed: " + e);
            }
        }

        /// <summary>Writes the values attached to this session object into its JSON. Any other
        /// object is left alone.</summary>
        private static void AsJObjectPostfix(GameSession __instance, JObject __result)
        {
            try
            {
                Attached a;
                if (__instance == null || __result == null || !_attached.TryGetValue(__instance, out a)) return;
                if (a.Keys.Count > 0) __result[EnvelopeKey] = Wrap((JObject)a.Keys.DeepClone(), a.Actor);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: adding the envelope to the session JSON failed: " + e);
            }
        }

        /// <summary>Every client but the host: reads and checks the envelope of the session the
        /// host sent, and attaches it to the rebuilt session object. Nothing becomes current until
        /// that object is activated.</summary>
        private static void FromJObjectPostfix(JObject __0, GameSession __result)
        {
            JToken token = null;
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
                token = __0 != null ? __0[EnvelopeKey] : null;
            }
            catch { }
            if (__result == null) return;

            JObject keys = null;
            int stamp = -1;
            int sender = _senderHook ? _event9Sender : -1;
            int master = MasterActor();
            try
            {
                string problem;
                keys = Unpack(token, out problem, out stamp);
                if (problem != null)
                {
                    Plugin.Log.LogWarning("Ectype: session data: ignored the host's envelope: " + problem + ".");
                    keys = null;
                }
                else if (keys != null)
                {
                    string why = WriterProblem(sender, stamp, master, InRoom);
                    if (why != null)
                    {
                        Plugin.Log.LogWarning("Ectype: session data: ignored the session data " + From(sender) + ": " +
                                              why + " (master is actor " + master + ").");
                        keys = null;
                    }
                    else if (keys.Count > 0)
                    {
                        Plugin.Log.LogInfo("Ectype: session data: received " + keys.Count + " key(s) from the host " +
                                           "(actor " + stamp + (sender > 0 ? "" : ", from the room cache") +
                                           ") with " + (__result.IsHub ? "hub" : "mission") + " session " +
                                           Short(__result.GameSessionID) + ".");
                    }
                }
            }
            catch (Exception e)
            {
                keys = null;
                Plugin.Log.LogError("Ectype: session data: reading the envelope from the session JSON failed: " + e);
            }
            try
            {
                Attach(__result, new Attached
                {
                    Keys = keys ?? new JObject(),
                    HostId = __result.GameSessionID,
                    IsHub = __result.IsHub,
                    Actor = stamp,
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: reading the envelope from the session JSON failed: " + e);
            }
        }

        private static string From(int sender)
        {
            return sender > 0 ? "live from actor " + sender : "from the room cache";
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

        /// <summary>The run is being saved: keep its values in the sidecar named by the id the save
        /// carries, which is the id a resume will use.</summary>
        private static void GetPreservedStatePostfix(GameSession __instance, PreservedGameSession __result)
        {
            try
            {
                Attached a;
                if (__instance == null || __result == null || !_attached.TryGetValue(__instance, out a)) return;
                if (a.IsHub || a.Keys.Count == 0) return;
                string id = __result.GameSessionID;
                if (!string.Equals(id, a.HostId, StringComparison.Ordinal) && _once.Add("saveid:" + id))
                    Plugin.Log.LogInfo("Ectype: session data: this run is saved under session " + Short(id) +
                                       ", not " + Short(a.HostId) + " (the host changed); its sidecar follows the " +
                                       "saved id.");
                SaveSidecar(id, a.Keys);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: session data: refreshing the saved-run sidecar failed: " + e);
            }
        }

        /// <summary>
        /// Null when an envelope stamped by actor <paramref name="stamp"/> and delivered by
        /// <paramref name="sender"/> may be the host's. A live event (sender above 0) must come
        /// from the actor it names, and that actor must be the master or no longer in the room.
        /// A cached event carries no sender (Photon delivers the room's global cache as actor 0),
        /// so its stamp must name the master or an actor who has left. The game hands the master
        /// role to another player only when the master leaves.
        /// </summary>
        internal static string WriterProblem(int sender, int stamp, int master, Func<int, bool> inRoom)
        {
            if (stamp <= 0) return "it carries no host stamp";
            if (sender > 0)
            {
                if (sender != stamp) return "it was sent by actor " + sender + " but names actor " + stamp + " as its host";
                if (sender != master && inRoom(sender)) return "actor " + sender + " is in the room and is not the master client";
                return null;
            }
            if (stamp == master || !inRoom(stamp)) return null;
            return "it names actor " + stamp + ", who is in the room and is not the master client";
        }

        private static bool InRoom(int actor)
        {
            Room room = PhotonNetwork.CurrentRoom;
            return room != null && room.GetPlayer(actor) != null;
        }

        private static int MasterActor()
        {
            Photon.Realtime.Player m = PhotonNetwork.MasterClient;
            return m != null ? m.ActorNumber : -1;
        }

        // ------------------------------------------------------------------ host capture

        private static Attached Capture(GameSession session)
        {
            string id = session.GameSessionID;
            int actor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
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
            JObject envelope = Pack(items, refusals, actor);
            foreach (string r in refusals)
                Plugin.Log.LogError("Ectype: session data: " + r + "; left out of this session.");
            JObject keys = (JObject)envelope["keys"];
            if (keys.Count > 0)
                Plugin.Log.LogInfo("Ectype: session data: sending " + keys.Count + " key(s) with " +
                                   (session.IsHub ? "hub" : "mission") + " session " + Short(id) + " (" +
                                   Bytes(envelope) + " bytes" +
                                   (resumed ? ", " + restored + " restored from the saved run" : "") + ").");
            return new Attached { Keys = keys, HostId = id, IsHub = session.IsHub, Actor = actor };
        }

        // ------------------------------------------------------------------ current state

        private static void SetCurrent(GameSession session, Attached a)
        {
            SessionState prev = _current;
            SessionState next = null;
            if (session != null && a != null)
            {
                next = new SessionState { Session = session, Id = a.HostId, IsHub = session.IsHub, Keys = a.Keys };
                foreach (Entry e in _entries)
                {
                    JToken token;
                    object value;
                    if (a.Keys.TryGetValue(e.Key, out token) && Decode(e, token, out value))
                        next.Values[e.Key] = value;
                }
            }
            _current = next;
            if (next != null) _hadRoomState = true;

            if (next != null && next.Keys.Count > 0 && Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: session data: applied " + next.Keys.Count + " key(s) locally.");

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
                if (_hadRoomState)
                {
                    _hadRoomState = false;
                    if (_current != null) SetCurrent(null, null);
                    HostValueRoom.Reset();
                    foreach (IHostValue hv in HostValues) hv.LeftRoom();
                }
                return;
            }
            if (HostValues.Count == 0) return;
            _hadRoomState = true;
            try { HostValueRoom.Tick(Time.unscaledTime); }
            catch (Exception e)
            {
                if (_once.Add("tick"))
                    Plugin.Log.LogError("Ectype: host values: update failed (reported once): " + e);
            }
        }

        /// <summary>Records who wrote the host-value room property. Photon raises event 253 for a
        /// property change with the writer as the event's sender, after it has merged the change
        /// into the room's copy.</summary>
        private static void OnPhotonEvent(EventData ev)
        {
            try
            {
                if (ev == null || ev.Code != 253 || HostValues.Count == 0) return;
                if (ev.Parameters.ContainsKey(253) && ev[253] is int && (int)ev[253] != 0) return;
                ExitGames.Client.Photon.Hashtable props = ev[251] as ExitGames.Client.Photon.Hashtable;
                object raw;
                if (props == null || !props.TryGetValue(HostValuesRoomProperty, out raw)) return;
                HostValueRoom.Written(ev.Sender, raw);
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

        /// <summary>
        /// Builds <c>{"v":1,"a":actor,"keys":{...}}</c>. An item over its own cap is left out.
        /// While the envelope is over <see cref="EnvelopeCapBytes"/>, the largest remaining item
        /// is left out (the later of equal sizes). Each refusal is described in
        /// <paramref name="refusals"/>, naming its key.
        /// </summary>
        internal static JObject Pack(IEnumerable<PackItem> items, List<string> refusals, int actor)
        {
            JObject keys = new JObject();
            List<KeyValuePair<string, int>> sizes = new List<KeyValuePair<string, int>>();
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
                sizes.Add(new KeyValuePair<string, int>(item.Key, size));
            }
            JObject envelope = Wrap(keys, actor);
            int total = Bytes(envelope);
            while (total > EnvelopeCapBytes && sizes.Count > 0)
            {
                int largest = 0;
                for (int i = 1; i < sizes.Count; i++)
                    if (sizes[i].Value >= sizes[largest].Value) largest = i;
                string key = sizes[largest].Key;
                refusals.Add("key '" + key + "' (" + sizes[largest].Value + " bytes, the largest) would bring the " +
                             "envelope to " + total + " bytes, over the " + EnvelopeCapBytes + "-byte cap");
                keys.Remove(key);
                sizes.RemoveAt(largest);
                total = Bytes(envelope);
            }
            return envelope;
        }

        /// <summary>The keys object of an envelope, or null when the token is absent. A problem
        /// (not an object, over the cap, another format) is described and null returned.
        /// <paramref name="actor"/> is the host stamp, or -1.</summary>
        internal static JObject Unpack(JToken envelope, out string problem, out int actor)
        {
            problem = null;
            actor = -1;
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
            long version;
            if (!TryLong(o["v"], out version) || version != FormatVersion)
            {
                problem = "its format is " + (o["v"] != null ? o["v"].ToString(Formatting.None) : "missing") +
                          ", this framework reads " + FormatVersion;
                return null;
            }
            JObject keys = o["keys"] as JObject;
            if (keys == null)
            {
                problem = "it has no keys object";
                return null;
            }
            long a;
            if (TryLong(o["a"], out a) && a > 0 && a <= int.MaxValue) actor = (int)a;
            return keys;
        }

        private static bool TryLong(JToken t, out long value)
        {
            value = 0;
            JValue v = t as JValue;
            if (v == null || v.Type != JTokenType.Integer) return false;
            try
            {
                value = Convert.ToInt64(v.Value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static JObject Wrap(JObject keys, int actor)
        {
            return new JObject { ["v"] = FormatVersion, ["a"] = actor, ["keys"] = keys };
        }

        internal static int Bytes(JToken token)
        {
            return token == null ? 0 : Encoding.UTF8.GetByteCount(token.ToString(Formatting.None));
        }

        internal static int Bytes(string s)
        {
            return s == null ? 0 : Encoding.UTF8.GetByteCount(s);
        }

        internal static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "(no id)" : id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // ------------------------------------------------------------------ host-value room format (pure)

        /// <summary>The room property value: <c>{"a":&lt;master actor&gt;,"v":{key: value, ...}}</c>.</summary>
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
            long a;
            if (!TryLong(o["a"], out a))
            {
                problem = "no actor stamp";
                return null;
            }
            if (a != master)
            {
                problem = "stamped by actor " + a + ", not the master client " + master;
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

        /// <summary>True when a JSON token holds a value of the scalar kind <paramref name="t"/>
        /// is, so a room value of another kind is never converted into it.</summary>
        internal static bool TokenFits(JToken token, Type t)
        {
            if (token == null) return false;
            Type under = Nullable.GetUnderlyingType(t);
            if (under != null)
            {
                if (token.Type == JTokenType.Null) return true;
                t = under;
            }
            JTokenType k = token.Type;
            if (t == typeof(string)) return k == JTokenType.String || k == JTokenType.Null;
            if (t == typeof(bool)) return k == JTokenType.Boolean;
            if (t.IsEnum) return k == JTokenType.Integer || k == JTokenType.String;
            if (t == typeof(char)) return k == JTokenType.String;
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
                return k == JTokenType.Float || k == JTokenType.Integer;
            if (t.IsPrimitive) return k == JTokenType.Integer;
            return false;
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

        /// <summary>Writes the whole file beside the target, then swaps it in, so the target is
        /// always either the previous file or the new one.</summary>
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
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
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
                if (new FileInfo(path).Length > EnvelopeCapBytes * 2)
                {
                    error = "the file is larger than any envelope";
                    return null;
                }
                JObject doc = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                long v;
                if (!TryLong(doc["v"], out v) || v != FormatVersion ||
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

        /// <summary>Deletes leftover temporary files, then sidecars last written more than
        /// <paramref name="maxAge"/> ago, then all but the newest <paramref name="maxCount"/>. Only
        /// files named by a session id are touched. Returns how many were deleted.</summary>
        internal static int PruneSidecars(string dir, TimeSpan maxAge, int maxCount, DateTime nowUtc)
        {
            if (!Directory.Exists(dir)) return 0;
            int removed = 0;
            foreach (string path in Directory.GetFiles(dir, "*.json.tmp"))
            {
                string name = Path.GetFileName(path);
                if (!ValidSessionId(name.Substring(0, name.Length - ".json.tmp".Length))) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch { }
            }
            List<FileInfo> files = new List<FileInfo>();
            foreach (string path in Directory.GetFiles(dir, "*.json"))
                if (ValidSessionId(Path.GetFileNameWithoutExtension(path))) files.Add(new FileInfo(path));
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
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

    /// <summary>
    /// The hub channel of every <see cref="HostValue{T}"/>: one room property,
    /// <see cref="SessionData.HostValuesRoomProperty"/>, holding
    /// <c>{"a":&lt;master actor&gt;,"v":{key: value, ...}}</c>. The host rebuilds it only when a
    /// value changes or the master changes, and writes it when the room's copy differs. A client
    /// accepts it when the stamp names the current master and the writer Photon reported for its
    /// last change, if any, is the master.
    /// </summary>
    internal static class HostValueRoom
    {
        internal const int CapBytes = SessionData.EnvelopeCapBytes;
        private const float RepublishSeconds = 2f;
        private static readonly object Unseen = new object();

        // host
        private static bool _dirty = true;
        private static int _builtFor = int.MinValue;
        private static string _desired;
        private static string _published;
        private static float _nextPublish;

        // client
        private static object _seenRaw = Unseen;
        private static int _seenMaster = int.MinValue;
        private static JObject _accepted;
        private static int _version;
        private static int _foreignWriter;
        private static object _foreignRaw = Unseen;

        private static readonly HashSet<string> _once = new HashSet<string>(StringComparer.Ordinal);

        internal static void MarkDirty() { _dirty = true; }

        /// <summary>The host's value for <paramref name="key"/> from the room, and a number that
        /// changes whenever the accepted property changes.</summary>
        internal static bool TryGet(string key, out JToken token, out int version)
        {
            version = _version;
            token = _accepted != null ? _accepted[key] : null;
            return token != null;
        }

        internal static void Tick(float now)
        {
            Room room = PhotonNetwork.CurrentRoom;
            if (room == null) return;
            object raw;
            if (!room.CustomProperties.TryGetValue(SessionData.HostValuesRoomProperty, out raw)) raw = null;
            if (PhotonNetwork.IsMasterClient)
            {
                if (!SessionData.InMission) Publish(room, raw, now);
            }
            else Read(raw, room.MasterClientId);
            foreach (IHostValue hv in SessionData.HostValues) hv.Recompute();
        }

        private static void Publish(Room room, object current, float now)
        {
            int me = PhotonNetwork.LocalPlayer.ActorNumber;
            if (_dirty || _builtFor != me) Build(me);
            if (_desired == null) return;
            if (_desired.Equals(current as string)) return;
            if (_desired == _published && now < _nextPublish) return;
            room.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { SessionData.HostValuesRoomProperty, _desired } });
            _published = _desired;
            _nextPublish = now + RepublishSeconds;
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: host values published to the room" +
                                   (current != null ? " (the room held another value)" : "") + ".");
        }

        private static void Build(int me)
        {
            _dirty = false;
            _builtFor = me;
            JObject values = new JObject();
            List<KeyValuePair<string, int>> sizes = new List<KeyValuePair<string, int>>();
            foreach (IHostValue hv in SessionData.HostValues)
            {
                JToken token;
                try { token = hv.HostToken(); }
                catch (Exception e)
                {
                    if (_once.Add("ser:" + hv.Key))
                        Plugin.Log.LogError("Ectype: host value '" + hv.Key + "' could not be serialised; not " +
                                            "published in the hub: " + e.Message);
                    continue;
                }
                int size = SessionData.Bytes(token);
                if (size > SessionData.DefaultKeyCapBytes)
                {
                    if (_once.Add("size:" + hv.Key))
                        Plugin.Log.LogError("Ectype: host value '" + hv.Key + "' is over the " +
                                            SessionData.DefaultKeyCapBytes + "-byte cap; not published in the hub.");
                    continue;
                }
                values[hv.Key] = token;
                sizes.Add(new KeyValuePair<string, int>(hv.Key, size));
            }
            string desired = SessionData.PackRoomValue(me, values);
            while (SessionData.Bytes(desired) > CapBytes && sizes.Count > 0)
            {
                int largest = 0;
                for (int i = 1; i < sizes.Count; i++)
                    if (sizes[i].Value >= sizes[largest].Value) largest = i;
                if (_once.Add("room:" + sizes[largest].Key))
                    Plugin.Log.LogError("Ectype: host value '" + sizes[largest].Key + "' (" + sizes[largest].Value +
                                        " bytes, the largest) left out of the hub room property: it would bring it to " +
                                        SessionData.Bytes(desired) + " bytes, over the " + CapBytes + "-byte cap.");
                values.Remove(sizes[largest].Key);
                sizes.RemoveAt(largest);
                desired = SessionData.PackRoomValue(me, values);
            }
            _desired = values.Count > 0 ? desired : null;
        }

        private static void Read(object raw, int master)
        {
            if (Equals(raw, _seenRaw) && master == _seenMaster) return;
            _seenMaster = master;

            if (raw != null && _foreignWriter > 0 && _foreignWriter != master && Equals(raw, _foreignRaw))
            {
                if (_once.Add("foreign:" + _foreignWriter))
                    Plugin.Log.LogWarning("Ectype: host values: ignored a room-property write from actor " +
                                          _foreignWriter + ", who is not the master client (" + master + ").");
                _seenRaw = raw;
                return; // keep the last accepted value
            }
            _seenRaw = raw;

            string problem;
            JToken v = SessionData.UnpackRoomValue(raw, master, CapBytes, out problem);
            JObject values = v as JObject;
            if (values == null && v != null) problem = "its value is not an object";
            if (values == null && raw != null && Plugin.Verbose && _once.Add("unused:" + problem))
                Plugin.Log.LogInfo("Ectype: host values: room property not used (" + problem + ").");
            _accepted = values;
            _version++;
        }

        internal static void Written(int sender, object raw)
        {
            _foreignWriter = sender;
            _foreignRaw = raw;
            _seenRaw = Unseen; // decide again at the next tick
            Photon.Realtime.Player me = PhotonNetwork.LocalPlayer;
            if (PhotonNetwork.IsMasterClient && me != null && sender > 0 && sender != me.ActorNumber)
            {
                _nextPublish = 0f; // write the host's value back at the next tick
                if (_once.Add("repair:" + sender))
                    Plugin.Log.LogWarning("Ectype: host values: actor " + sender + " wrote the room property; " +
                                          "republishing the host's values.");
            }
        }

        internal static void Reset()
        {
            _dirty = true;
            _builtFor = int.MinValue;
            _desired = null;
            _published = null;
            _nextPublish = 0f;
            _seenRaw = Unseen;
            _seenMaster = int.MinValue;
            _accepted = null;
            _version++;
            _foreignWriter = 0;
            _foreignRaw = Unseen;
            _once.Clear();
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
        void Recompute();
        JToken HostToken();
        void LeftRoom();
    }

    /// <summary>
    /// A config entry read as the host's value. Get one from
    /// <see cref="SessionData.HostValue{T}"/>. Read <see cref="Value"/> wherever the mod would read
    /// the entry's own value for a decision the host owns.
    /// </summary>
    public sealed class HostValue<T> : IHostValue
    {
        private readonly bool _live;
        private T _value;
        private bool _fromHost;
        private HostValueSource _source;
        private bool _computed;

        private int _roomVersion = int.MinValue;
        private bool _roomHas;
        private T _roomValue;
        private bool _warnedRoom;

        internal HostValue(string key, BepInEx.Configuration.ConfigEntry<T> entry, bool live)
        {
            Key = key;
            Entry = entry;
            _live = live;
            if (live)
                entry.SettingChanged += (s, e) =>
                {
                    HostValueRoom.MarkDirty();
                    Recompute();
                };
        }

        /// <summary>The key, in the session data and in the hub room property.</summary>
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

        void IHostValue.Recompute() { Recompute(); }

        JToken IHostValue.HostToken()
        {
            T v = Entry.Value;
            return v != null ? JToken.FromObject(v) : JValue.CreateNull();
        }

        /// <summary>Applies the precedence in force: in a mission, the session's frozen value; in
        /// the hub, the host's own config on the host, else the host's room property, else the hub
        /// session's value; this client's config when none of those exists.</summary>
        internal void Recompute()
        {
            T value = Entry.Value;
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
                else if (RoomValue(out T room))
                {
                    value = room;
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

        private bool RoomValue(out T value)
        {
            JToken token;
            int version;
            bool present = HostValueRoom.TryGet(Key, out token, out version);
            if (version != _roomVersion)
            {
                _roomVersion = version;
                _roomHas = false;
                if (present)
                {
                    try
                    {
                        if (!SessionData.TokenFits(token, typeof(T))) throw new FormatException("a JSON " + token.Type);
                        _roomValue = token.ToObject<T>();
                        _roomHas = true;
                    }
                    catch (Exception e)
                    {
                        if (!_warnedRoom)
                        {
                            _warnedRoom = true;
                            Plugin.Log.LogWarning("Ectype: host value '" + Key + "': the host's room value does not " +
                                                  "read as " + typeof(T).Name + ": " + e.Message);
                        }
                    }
                }
            }
            value = _roomValue;
            return _roomHas;
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

        void IHostValue.LeftRoom()
        {
            _roomVersion = int.MinValue;
            _roomHas = false;
            _warnedRoom = false;
            Recompute();
        }
    }
}
