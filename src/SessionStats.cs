using System;
using System.Collections.Generic;
using System.Globalization;

using Gameplay.Tags;
using Gameplay.Utilities;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// A run-wide stat collection the framework owns, one per session (the hub included),
    /// holding every stat declared for the <c>session</c> collection: the framework's
    /// <see cref="FrameworkStats.HostileSpawnScale"/> and any a mod's stats file declares.
    ///
    /// The host is authoritative. As the host starts a session it builds a fresh collection,
    /// raises <see cref="Opening"/> for contributors, and publishes the composed value of every
    /// session stat through <see cref="SessionData"/>, so the values are frozen with the session
    /// and reach late joiners with it. Every other client builds its own collection with each
    /// stat's base set to the host's value. The collection is dropped when the next session
    /// arrives or the client leaves the room.
    ///
    /// Publishing composed values is a choice. The alternative is to replicate the inputs: every
    /// contribution would come from data every client already holds (mutators, perks, frozen host
    /// config), each client would compose the same value itself, and a reader could run anywhere
    /// with tooltips matching. That needs every contributor to be replicated data, which code
    /// contributions on the host are not, so the host's composition is the single truth and
    /// clients copy it.
    ///
    /// The game's StatTagCollection constructor reads its holder's tags, so the collection is
    /// held by a component on a DontDestroyOnLoad GameObject named <see cref="HolderObjectName"/>.
    /// The component implements the game's IStatCollectionHolder, so a mod that does not
    /// reference the framework reaches the collection with
    /// <c>GameObject.Find("Ectype.SessionStats").GetComponent&lt;IStatCollectionHolder&gt;().Stats</c>.
    /// </summary>
    public static class SessionStats
    {
        /// <summary>The session data key holding <c>{"&lt;id&gt;": value}</c> for every session stat.</summary>
        public const string SessionDataKey = "framework/sessionStats";

        /// <summary>The name of the GameObject that holds the session collection.</summary>
        public const string HolderObjectName = "Ectype.SessionStats";

        internal const int MaxBytes = 8192;

        /// <summary>The most stats the session collection holds: about 25 bytes each in the
        /// published values keeps them well inside <see cref="MaxBytes"/>.</summary>
        public const int MaxStats = 128;

        /// <summary>
        /// Raised on the host only, once per session, while the host starts the session and
        /// before the values are published. Contribute here: a contribution made inside this event
        /// is in the values every client receives. Each subscriber runs in its own try/catch.
        /// </summary>
        public static event Action Opening;

        /// <summary>The current session's collection, or null outside a session or when session
        /// data is unavailable. Inside <see cref="Opening"/>, the collection being opened.</summary>
        public static StatTagCollection Collection
        {
            get
            {
                Live live = _pending ?? _current;
                return live != null ? live.Collection : null;
            }
        }

        /// <summary>The id of the session the collection belongs to, or null (also inside
        /// <see cref="Opening"/>, before the session's id is known).</summary>
        public static string SessionId
        {
            get { return _current != null ? _current.SessionId : null; }
        }

        /// <summary>State for the F5 status page.</summary>
        public static string Status = "not installed";

        private sealed class Live
        {
            internal string SessionId;
            internal StatTagCollection Collection;
            internal bool Host;
            internal readonly HashSet<int> WarnedLate = new HashSet<int>();
        }

        private static Live _current;
        private static Live _pending;
        private static bool _installed;
        private static bool _warnedClient;
        private static bool _warnedClosed;
        private static SessionStatHolder _holder;
        private static readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ contributions

        /// <summary>
        /// Host only: adds <paramref name="modifier"/> to session stat <paramref name="statId"/>,
        /// removable with <see cref="Withdraw"/> by <paramref name="source"/>. Inside <see cref="Opening"/> it targets the collection being opened and
        /// reaches every client; later in the session it changes the host's value only, with one
        /// warning per stat. Returns false, with one warning, on a client, outside a session, or
        /// for an id that is not a session stat.
        /// </summary>
        public static bool Contribute(int statId, FloatModifier modifier, IModifierSource source)
        {
            if (modifier == null) throw new ArgumentNullException("modifier");
            if (source == null) throw new ArgumentNullException("source");
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
            {
                if (!_warnedClient)
                {
                    _warnedClient = true;
                    Plugin.Log.LogWarning("Ectype: session stats: a contribution on a client was refused; only " +
                                          "the host contributes, and clients receive the host's values.");
                }
                return false;
            }
            Live target = _pending ?? _current;
            if (target == null)
            {
                if (!_warnedClosed)
                {
                    _warnedClosed = true;
                    Plugin.Log.LogWarning("Ectype: session stats: a contribution with no session open was refused; " +
                                          "contribute from SessionStats.Opening.");
                }
                return false;
            }
            if (!ContributeTo(target.Collection, statId, modifier, source))
            {
                Plugin.Log.LogWarning("Ectype: session stats: stat " + Describe(statId) + " is not a session stat; " +
                                      "the contribution was refused.");
                return false;
            }
            if (target == _current && target.WarnedLate.Add(statId))
                Plugin.Log.LogWarning("Ectype: session stats: " + Describe(statId) + " changed after the session " +
                                      "started; clients keep the session-start value until the next session.");
            return true;
        }

        /// <summary>Removes every modifier <paramref name="source"/> added to the open
        /// collection. Host only; a no-op elsewhere.</summary>
        public static void Withdraw(IModifierSource source)
        {
            if (source == null) return;
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return;
            Live target = _pending ?? _current;
            if (target != null) target.Collection.RemoveModifier(source);
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Called at plugin Awake, after the session data patches.</summary>
        internal static void Install()
        {
            if (_installed) return;
            EnsureHolder();
            _installed = SessionData.RegisterCore<Dictionary<string, float>>(SessionDataKey, Produce, Apply,
                new SessionDataOptions { MaxBytes = MaxBytes, KeepOnResume = false }, false);
            Status = _installed ? "no session" : "inert: session data unavailable";
            if (!_installed)
                Plugin.Log.LogWarning("Ectype: session stats: inert this session (session data unavailable); " +
                                      "every session stat reads its declared default.");
        }

        /// <summary>Host: builds the session's collection, lets contributors in, and returns the
        /// composed values. Runs as session data produces the host's values for a new session.</summary>
        private static Dictionary<string, float> Produce()
        {
            return Produce(HostHandlers());
        }

        /// <summary>What the host runs while it opens the collection, in order: the contributions
        /// of the mutators the session is about to activate, then every <see cref="Opening"/>
        /// subscriber.</summary>
        internal static Action HostHandlers()
        {
            Action mutators = SessionStatMutators.ContributeAtOpening;
            return mutators + Opening;
        }

        /// <summary>
        /// Discards the previous session's collection, makes the new one current for
        /// <see cref="Collection"/> and <see cref="Contribute"/>, raises
        /// <paramref name="handlers"/>, and returns the composed values.
        /// </summary>
        internal static Dictionary<string, float> Produce(Action handlers)
        {
            Close();
            Live live = new Live { Collection = NewCollection(ModStats.DeclaredFor(StatCollectionKind.Session)), Host = true };
            _pending = live;
            if (handlers != null)
            {
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    try { ((Action)d)(); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Ectype: SessionStats.Opening subscriber " + d.Method.DeclaringType + "." +
                                            d.Method.Name + " threw: " + e);
                    }
                }
            }
            return Compose(live.Collection);
        }

        /// <summary>Every client, the host included: the session's values arrived.</summary>
        private static void Apply(Dictionary<string, float> values)
        {
            Live live;
            if (_pending != null && (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient))
            {
                live = _pending;
                _pending = null;
            }
            else
            {
                _pending = null;
                live = new Live { Collection = NewCollection(ModStats.DeclaredFor(StatCollectionKind.Session)) };
                ApplyHostValues(live.Collection, values);
            }
            live.SessionId = SessionData.SessionId;
            Open(live, live.Host ? "as host" : "with the host's values");
        }

        /// <summary>Drops the collection when its session ends, and opens one at the declared
        /// defaults for a session that carried no values.</summary>
        internal static void Poll()
        {
            if (!_installed) return;
            string sid = SessionData.SessionId;
            if (_current != null && !string.Equals(_current.SessionId, sid, StringComparison.Ordinal)) Close();
            if (_current != null || sid == null) return;

            Live live;
            if (_pending != null && (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient))
            {
                // The host produced values the session data then left out.
                live = _pending;
                _pending = null;
                Plugin.Log.LogError("Ectype: session stats: session " + Short(sid) + " was sent without its " +
                                    "session stats; clients read the declared defaults.");
            }
            else
            {
                live = new Live { Collection = NewCollection(ModStats.DeclaredFor(StatCollectionKind.Session)) };
                if (_warnedMissing.Add(sid))
                    Plugin.Log.LogWarning("Ectype: session stats: session " + Short(sid) + " carried no session " +
                                          "stats; every session stat reads its declared default.");
            }
            live.SessionId = sid;
            Open(live, "at the declared defaults");
        }

        private static void Open(Live live, string how)
        {
            if (_current != null) Close();
            _current = live;
            EnsureHolder();
            Status = "session " + Short(live.SessionId) + (live.Host ? ", host" : ", client");
            Plugin.Log.LogInfo("Ectype: session stats: opened for session " + Short(live.SessionId) + " " + how +
                               " (" + Summary(live.Collection) + ").");
        }

        private static void Close()
        {
            Live old = _current;
            _current = null;
            Status = "no session";
            if (old == null) return;
            Dispose(old.Collection);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: session stats: closed for session " + Short(old.SessionId) + ".");
        }

        // ------------------------------------------------------------------ collection helpers

        /// <summary>A fresh collection holding every stat in <paramref name="decls"/> at its
        /// declared default.</summary>
        internal static StatTagCollection NewCollection(IList<ModStatDecl> decls)
        {
            StatTagCollection c = new StatTagCollection(HolderForCollection());
            foreach (ModStatDecl d in decls)
            {
                if (c.Stats.ContainsKey(d.Id)) continue;
                ModifiableFloat f = new ModifiableFloat(d.Default);
                c.RegisterStat(d.Id, ref f);
            }
            return c;
        }

        /// <summary>Adds one modifier; false when the collection does not hold the stat.</summary>
        internal static bool ContributeTo(StatTagCollection c, int statId, FloatModifier modifier,
                                          IModifierSource source)
        {
            if (c == null || c.Stats == null || !c.Stats.ContainsKey(statId)) return false;
            StatMod mod = new StatMod(modifier, statId);
            if (source != null) mod.SetSource(source);
            c.ApplyModifier(mod);
            return true;
        }

        /// <summary>The composed value of every float stat, keyed by the id in invariant
        /// decimal.</summary>
        internal static Dictionary<string, float> Compose(StatTagCollection c)
        {
            Dictionary<string, float> values = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, StatBase> kv in c.Stats)
            {
                ModifiableFloat f = kv.Value.Data as ModifiableFloat;
                if (f != null) values[kv.Key.ToString(CultureInfo.InvariantCulture)] = f.Value;
            }
            return values;
        }

        /// <summary>
        /// Sets each stat in <paramref name="values"/> to the host's value by setting its base,
        /// which reproduces the value exactly: a client's collection carries no modifiers. A stat
        /// the host published and this client does not declare is registered at the host's value,
        /// so a reader by id still gets it. Unparseable entries are skipped.
        /// </summary>
        internal static void ApplyHostValues(StatTagCollection c, Dictionary<string, float> values)
        {
            if (values == null) return;
            foreach (KeyValuePair<string, float> kv in values)
            {
                int id;
                if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ||
                    float.IsNaN(kv.Value) || float.IsInfinity(kv.Value))
                    continue;
                StatBase stat;
                if (!c.Stats.TryGetValue(id, out stat))
                {
                    ModifiableFloat f = new ModifiableFloat(kv.Value);
                    c.RegisterStat(id, ref f);
                    continue;
                }
                // SetBaseValue, not the collection's OverrideBaseValue: that one writes the field
                // without marking the value for recalculation, so a value read before stays stale.
                ModifiableFloat cur = stat.Data as ModifiableFloat;
                if (cur != null) cur.SetBaseValue(kv.Value);
            }
        }

#if ECTYPE_DEV
        /// <summary>Dev self-test: drops a collection <see cref="Produce(Action)"/> left pending.</summary>
        internal static void DiscardPendingForTest()
        {
            if (_pending != null) Dispose(_pending.Collection);
            _pending = null;
        }
#endif

        /// <summary>Removes every modifier from the collection, by source, which also releases
        /// the collection's subscriptions to dynamic values and conditions.</summary>
        internal static void Dispose(StatTagCollection c)
        {
            if (c == null) return;
            HashSet<IModifierSource> sources = new HashSet<IModifierSource>();
            foreach (StatMod m in c.ActiveModifiers) if (m != null && m.Mod != null && m.Mod.Source != null) sources.Add(m.Mod.Source);
            foreach (StatMod m in c.InactiveModifiers) if (m != null && m.Mod != null && m.Mod.Source != null) sources.Add(m.Mod.Source);
            foreach (IModifierSource s in sources)
            {
                try { c.RemoveModifier(s); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Ectype: session stats: removing a modifier at session end threw: " +
                                          e.Message);
                }
            }
        }

        private static string Summary(StatTagCollection c)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, float> kv in Compose(c))
            {
                int id = int.Parse(kv.Key, CultureInfo.InvariantCulture);
                parts.Add(Describe(id) + "=" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
            }
            return parts.Count == 0 ? "no stats" : string.Join(", ", parts.ToArray());
        }

        private static string Describe(int id)
        {
            string name = StatRegistry.NameOf(id);
            return name ?? id.ToString(CultureInfo.InvariantCulture);
        }

        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(none)";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // ------------------------------------------------------------------ holder

        private static void EnsureHolder()
        {
            if (_holder != null) return;
            GameObject go = new GameObject(HolderObjectName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            // DontSave keeps it through scene unloads; it is not hidden, so GameObject.Find finds it.
            go.hideFlags = HideFlags.DontSave;
            _holder = go.AddComponent<SessionStatHolder>();
        }

        private static IStatCollectionHolder HolderForCollection()
        {
            EnsureHolder();
            if (_holder != null) return _holder;
            return TaglessHolder.Instance;
        }

        /// <summary>Stands in when the GameObject cannot be made (a self-test outside a scene).</summary>
        private sealed class TaglessHolder : IStatCollectionHolder
        {
            internal static readonly TaglessHolder Instance = new TaglessHolder();
            public CsTag[] CsTags { get { return SessionStatHolder.NoTags; } }
            public Transform HolderTransform { get { return null; } }
            public StatTagCollection Stats { get { return Collection; } }
        }
    }

    /// <summary>
    /// The component that holds the session collection, on the GameObject named
    /// <see cref="SessionStats.HolderObjectName"/>. It carries no tags, so a tag-filtered
    /// modifier never activates in the session collection.
    /// </summary>
    public sealed class SessionStatHolder : MonoBehaviour, IStatCollectionHolder
    {
        internal static readonly CsTag[] NoTags = new CsTag[0];

        /// <summary>No tags.</summary>
        public CsTag[] CsTags { get { return NoTags; } }

        /// <summary>This component's transform. No game code reads a holder's transform.</summary>
        public Transform HolderTransform { get { return transform; } }

        /// <summary>The current session collection, or null.</summary>
        public StatTagCollection Stats { get { return SessionStats.Collection; } }
    }
}
