using System;
using System.Collections.Generic;

using CG.Client;
using CG.Space;
using UnityEngine;

namespace Ectype
{
    /// <summary>What a player is trying to do, as the interaction gate's shared classifier reads
    /// it. One value per query.</summary>
    public enum InteractionKind
    {
        /// <summary>Anything the classifier does not name below.</summary>
        Other = 0,
        /// <summary>A seat that is neither the helm nor a turret.</summary>
        Seat,
        /// <summary>The helm's seat, or the helm module itself.</summary>
        Helm,
        /// <summary>A turret's seat, or the turret module itself.</summary>
        Turret,
        /// <summary>A terminal: a module terminal, a world-space terminal screen or the astral map.</summary>
        Terminal,
        /// <summary>A door's own control, an airlock door's button, or (owner side) a door or
        /// airlock door open request.</summary>
        Door,
        /// <summary>An airlock's pressurise lever, the airlock itself, or (owner side) a request
        /// to cycle the airlock. <see cref="InteractionKinds.IsAirlockControl"/> covers this and
        /// <see cref="AirlockSafety"/>.</summary>
        AirlockPressurize,
        /// <summary>An airlock's safety override switch, or (owner side) a request to change
        /// it.</summary>
        AirlockSafety,
        /// <summary>A module's own power switch (not the central computer's), a switch that
        /// controls a power drain, or (owner side) a request to switch a power drain. Every
        /// power kind is covered by <see cref="InteractionKinds.IsPowerSwitch"/>.</summary>
        ModulePowerSwitch,
        /// <summary>The central computer's power switch (the whole ship's power), a switch that
        /// controls a power system, or (owner side) a request to switch a power system.</summary>
        ShipPowerSwitch,
        /// <summary>A power line's switch (a power propagator), or (owner side) a request to
        /// switch one.</summary>
        PowerLineSwitch,
        /// <summary>A power breaker, a switch on or under one, or (owner side) a request to change
        /// a breaker's synced state.</summary>
        Breaker,
        /// <summary>A switch outside any module that controls no value the classifier can
        /// see.</summary>
        OtherPowerSwitch,
        /// <summary>A System Enhancement panel: its switch, its minigame, or a request to change
        /// its state.</summary>
        EnhancementPanel,
        /// <summary>Picking up a loose item, or taking one from another carrier that is not a
        /// socket.</summary>
        CarryablePickup,
        /// <summary>Putting the carried item into a socket.</summary>
        SocketInsert,
        /// <summary>Taking an item out of a socket, or ejecting it.</summary>
        SocketRelease,
        /// <summary>The module upgrade tool working on a module.</summary>
        ModuleUpgrade,
        /// <summary>A clone tube.</summary>
        CloneTube,
        /// <summary>A minigame that is not a System Enhancement panel's: a repair's or a
        /// defect's.</summary>
        Minigame,
        /// <summary>The periscope.</summary>
        Periscope,
    }

    /// <summary>The coarse questions over <see cref="InteractionKind"/>: which kinds belong to
    /// one family of controls. Callable as <c>InteractionKinds.IsPowerSwitch(q.Kind)</c> or
    /// <c>q.Kind.IsPowerSwitch()</c>.</summary>
    public static class InteractionKinds
    {
        /// <summary>Any power control: <see cref="InteractionKind.ModulePowerSwitch"/>,
        /// <see cref="InteractionKind.ShipPowerSwitch"/>, <see cref="InteractionKind.PowerLineSwitch"/>,
        /// <see cref="InteractionKind.Breaker"/> or <see cref="InteractionKind.OtherPowerSwitch"/>.</summary>
        public static bool IsPowerSwitch(this InteractionKind kind)
        {
            switch (kind)
            {
                case InteractionKind.ModulePowerSwitch:
                case InteractionKind.ShipPowerSwitch:
                case InteractionKind.PowerLineSwitch:
                case InteractionKind.Breaker:
                case InteractionKind.OtherPowerSwitch:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>An airlock's own control: <see cref="InteractionKind.AirlockPressurize"/> or
        /// <see cref="InteractionKind.AirlockSafety"/>. An airlock's door button is
        /// <see cref="InteractionKind.Door"/>.</summary>
        public static bool IsAirlockControl(this InteractionKind kind)
        {
            return kind == InteractionKind.AirlockPressurize || kind == InteractionKind.AirlockSafety;
        }
    }

    /// <summary>Which check a query belongs to.</summary>
    public enum GateSide
    {
        /// <summary>The acting player's own game, before anything is sent: no highlight, no
        /// prompt, and a warning on a refused use. It covers the host's own actions, which never
        /// cross the network. A modified client can skip it.</summary>
        Client,

        /// <summary>The game of whoever owns the part (the host, or a pilot holding a seat's
        /// views), on a request that arrived over the network; for an ownership change, the
        /// object's owner, or the host when no owner is present. A refusal drops the request and
        /// shows nothing. Never asked about the host, nor about a request the owner's own game
        /// sent itself.</summary>
        Owner,
    }

    /// <summary>
    /// One interaction to judge. Built by the framework at each chokepoint; a mod with a
    /// chokepoint of its own may build one and pass it to
    /// <see cref="InteractionGate.Evaluate"/>.
    /// </summary>
    public sealed class GateQuery
    {
        private PlayerControlledShip _ship;
        private bool _shipResolved;
        private CG.Game.Player.Player _character;
        private bool _characterResolved;

        /// <summary>A query. <paramref name="interactable"/> is resolved as the classifier
        /// resolves it: an item in a socket becomes the socket.</summary>
        public GateQuery(Photon.Realtime.Player actor, Component interactable, InteractionKind kind,
                         GateSide side)
        {
            Actor = actor;
            Interactable = interactable;
            Kind = kind;
            Side = side;
        }

        /// <summary>The acting player. Null only outside a room.</summary>
        public Photon.Realtime.Player Actor { get; private set; }

        /// <summary>The acting player's actor number, or 0 outside a room.</summary>
        public int ActorNumber { get { return Actor != null ? Actor.ActorNumber : 0; } }

        /// <summary>True when the acting player is the room's host.</summary>
        public bool ActorIsHost { get { return Actor != null && Actor.IsMasterClient; } }

        /// <summary>The acting player's character, or null (not spawned, spectating).</summary>
        public CG.Game.Player.Player Character
        {
            get
            {
                if (!_characterResolved)
                {
                    _characterResolved = true;
                    _character = Actor != null ? Topology.CharacterOf(Actor.ActorNumber) : null;
                }
                return _character;
            }
        }

        /// <summary>What is being used: the interactable the raycast found, the activator a
        /// station raised, the seat, the socket, the door, the airlock, the power component, the
        /// panel, the module being upgraded, or the network object an ownership event names.</summary>
        public Component Interactable { get; private set; }

        /// <summary>The player ship <see cref="Interactable"/> belongs to
        /// (<see cref="Topology.ShipOfPart"/>), or null.</summary>
        public PlayerControlledShip Ship
        {
            get
            {
                if (!_shipResolved)
                {
                    _shipResolved = true;
                    _ship = Topology.ShipOfPart(Interactable);
                }
                return _ship;
            }
        }

        /// <summary>What the player is trying to do.</summary>
        public InteractionKind Kind { get; private set; }

        /// <summary>Which check is asking.</summary>
        public GateSide Side { get; private set; }

        /// <summary>A short description for log lines.</summary>
        public override string ToString()
        {
            return Kind + " " + (Interactable != null ? Interactable.name : "(none)") + " by player " +
                   ActorNumber + " (" + Side + ")";
        }
    }

    /// <summary>
    /// A gate's answer. <see cref="Allowed"/> lets the interaction through; any other instance
    /// refuses it.
    /// </summary>
    public sealed class Refusal
    {
        /// <summary>The one value that allows.</summary>
        public static readonly Refusal Allowed = new Refusal();

        private Refusal() { IsAllowed = true; }

        /// <summary>A refusal. <paramref name="reason"/> goes to the log;
        /// <paramref name="text"/> is the warning the acting player sees, or null to refuse
        /// without a warning.</summary>
        public Refusal(string reason, string text)
        {
            Reason = reason ?? string.Empty;
            Text = string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>A refusal with a warning.</summary>
        public static Refusal Because(string reason, string text) { return new Refusal(reason, text); }

        /// <summary>A refusal without a warning.</summary>
        public static Refusal Silently(string reason) { return new Refusal(reason, null); }

        /// <summary>True only for <see cref="Allowed"/>.</summary>
        public bool IsAllowed { get; private set; }

        /// <summary>Why, for the log. Empty for <see cref="Allowed"/>.</summary>
        public string Reason { get; private set; }

        /// <summary>The player-facing warning, or null.</summary>
        public string Text { get; private set; }

        /// <summary>True when the refusal shows no warning.</summary>
        public bool Silent { get { return Text == null; } }
    }

    /// <summary>
    /// Refuses interactions on rules registered by mods. Each rule (a gate) sees one
    /// <see cref="GateQuery"/> and returns <see cref="Refusal.Allowed"/> or a refusal. Gates
    /// only refuse: the first refusal in registration order decides and its text is shown, so
    /// no gate can open what another closed. A gate that throws is logged once and counts as
    /// allowing.
    ///
    /// The framework asks twice for one interaction: on the acting player's client, for the
    /// prompt and the warning, and on the part owner's game when the request arrives, because a
    /// modified client can skip its own check. A gate must therefore give the same answer on
    /// every client that judges, reading only state they agree on: room properties, replicated
    /// statuses and abilities, factions, positions with slack. A gate that reads local-only state
    /// makes the client allow and the owner refuse: the player uses the part and nothing
    /// happens.
    /// </summary>
    public static class InteractionGate
    {
        private sealed class Entry
        {
            internal string Id;
            internal Func<GateQuery, Refusal> Gate;
            internal bool Faulted;
        }

        private static readonly List<Entry> Gates = new List<Entry>();
        private static Entry[] _snapshot = new Entry[0];
        private static readonly HashSet<string> Muted = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedOwnerRefusals = new HashSet<string>(StringComparer.Ordinal);
        private static string _lastClientLog;

        /// <summary>True while at least one gate is registered. Every chokepoint returns at once
        /// when it is false.</summary>
        public static bool AnyRegistered { get { return _snapshot.Length > 0; } }

        /// <summary>The registered gate ids, in registration order.</summary>
        public static IList<string> Registered
        {
            get
            {
                Entry[] s = _snapshot;
                string[] ids = new string[s.Length];
                for (int i = 0; i < s.Length; i++) ids[i] = s[i].Id;
                return ids;
            }
        }

        /// <summary>
        /// Adds a gate under a unique id, <c>"&lt;plugin GUID&gt;/&lt;name&gt;"</c> where the GUID is the
        /// registering plugin's BepInEx GUID. Returns false, and logs one error, when the id does not
        /// follow that form; and false, with a warning, when the gate is null or the id is already
        /// registered; unregister first to replace a gate.
        /// A gate is called on the main thread, possibly every frame while the player looks at
        /// something, so keep it cheap.
        /// </summary>
        public static bool Register(string id, Func<GateQuery, Refusal> gate)
        {
            if (string.IsNullOrEmpty(id) || gate == null)
            {
                Plugin.Log.LogWarning("Ectype: interaction gate: Register needs an id and a gate; ignored.");
                return false;
            }
            if (!Identifiers.RefuseId("interaction gate", id)) return false;

            for (int i = 0; i < Gates.Count; i++)
            {
                if (string.Equals(Gates[i].Id, id, StringComparison.Ordinal))
                {
                    Plugin.Log.LogWarning("Ectype: interaction gate '" + id + "' is already registered; " +
                                          "unregister it first. This registration is ignored.");
                    return false;
                }
            }
            Gates.Add(new Entry { Id = id, Gate = gate });
            _snapshot = Gates.ToArray();
            Plugin.Log.LogInfo("Ectype: interaction gate '" + id + "' registered (" + Gates.Count + " gate" +
                               (Gates.Count == 1 ? "" : "s") + ").");
            return true;
        }

        /// <summary>Removes a gate. Returns false when no gate has that id.</summary>
        public static bool Unregister(string id)
        {
            for (int i = 0; i < Gates.Count; i++)
            {
                if (!string.Equals(Gates[i].Id, id, StringComparison.Ordinal)) continue;
                Gates.RemoveAt(i);
                _snapshot = Gates.ToArray();
                Plugin.Log.LogInfo("Ectype: interaction gate '" + id + "' unregistered (" + Gates.Count +
                                   " left).");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Keeps a gate's refusals but drops their warning text, for a mod that shows its own
        /// warning for the same rule. Applies to a gate registered later under that id too.
        /// </summary>
        public static void SuppressWarningsFor(string id, bool suppress = true)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (suppress) Muted.Add(id);
            else Muted.Remove(id);
        }

        /// <summary>
        /// Runs every gate on a query, in registration order, and returns the first refusal or
        /// <see cref="Refusal.Allowed"/>. Shows nothing and logs nothing: the framework's own
        /// chokepoints do that.
        /// </summary>
        public static Refusal Evaluate(GateQuery query)
        {
            string id;
            return EvaluateWithId(query, out id);
        }

        /// <summary>The kind the shared classifier gives a component (an item in a socket is
        /// the socket, and an item is its CarryableObject, as every chokepoint presents them). A socket reads as <see cref="InteractionKind.SocketRelease"/>; the
        /// framework's own chokepoints know whether the player is carrying something and say
        /// <see cref="InteractionKind.SocketInsert"/> when they are.</summary>
        public static InteractionKind Classify(Component component)
        {
            return InteractionClassifier.Classify(InteractionClassifier.Canonical(component), false);
        }

        internal static Refusal EvaluateWithId(GateQuery query, out string gateId)
        {
            gateId = null;
            Entry[] gates = _snapshot;
            if (query == null || gates.Length == 0) return Refusal.Allowed;
            for (int i = 0; i < gates.Length; i++)
            {
                Entry e = gates[i];
                Refusal r;
                try { r = e.Gate(query); }
                catch (Exception ex)
                {
                    if (!e.Faulted)
                    {
                        e.Faulted = true;
                        Plugin.Log.LogError("Ectype: interaction gate '" + e.Id + "' threw and counts as " +
                                            "allowing (logged once per gate): " + ex);
                    }
                    continue;
                }
                if (r == null || r.IsAllowed) continue;
                gateId = e.Id;
                return r;
            }
            return Refusal.Allowed;
        }

        /// <summary>
        /// The client-side verdict: evaluates, and on a refusal shows its text through the
        /// throttled warning when <paramref name="warn"/> is true. Returns true when refused. In
        /// verbose mode a refusal is logged when it differs from the last one logged (gate,
        /// object, kind), so a player looking at a refused part logs it once, not every frame.
        /// </summary>
        internal static bool RefuseOnClient(GateQuery query, bool warn)
        {
            string id;
            Refusal r = EvaluateWithId(query, out id);
            if (r.IsAllowed) return false;
            if (Plugin.Verbose)
            {
                string key = id + "|" + (query.Interactable != null ? query.Interactable.GetInstanceID() : 0) +
                             "|" + query.Kind;
                if (key != _lastClientLog)
                {
                    _lastClientLog = key;
                    Plugin.Log.LogInfo("Ectype: interaction gate '" + id + "' refused " + query +
                                       (r.Reason.Length > 0 ? ": " + r.Reason : "") + ".");
                }
            }
            if (warn && !r.Silent && !Muted.Contains(id)) GateWarnings.Show(r.Text);
            return true;
        }

        /// <summary>
        /// The owner-side verdict: evaluates and logs; shows nothing. The first refusal of each
        /// (gate, player, kind) is logged at info level, the rest in verbose mode only. Returns
        /// true when refused.
        /// </summary>
        internal static bool RefuseOnOwner(GateQuery query)
        {
            string id;
            Refusal r = EvaluateWithId(query, out id);
            if (r.IsAllowed) return false;
            string key = id + "|" + query.ActorNumber + "|" + query.Kind;
            if (LoggedOwnerRefusals.Count > 4096) LoggedOwnerRefusals.Clear();
            if (LoggedOwnerRefusals.Add(key))
                Plugin.Log.LogInfo("Ectype: interaction gate '" + id + "' refused " + query +
                                   (r.Reason.Length > 0 ? ": " + r.Reason : "") +
                                   " (further refusals of this kind to this player are logged in " +
                                   "verbose mode only).");
            else if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: interaction gate '" + id + "' refused " + query +
                                   (r.Reason.Length > 0 ? ": " + r.Reason : "") + ".");
            return true;
        }
    }

    /// <summary>
    /// The framework-wide warning throttle: one warning every 3 s at most, and the same text at
    /// most every 20 s, whichever gate refused. Two gates therefore cannot double the rate.
    /// Warnings go through the game's own quick-warning channel.
    /// </summary>
    internal static class GateWarnings
    {
        internal const float GapSeconds = 3f;
        internal const float SameTextSeconds = 20f;

        private static readonly Dictionary<string, float> LastShown = new Dictionary<string, float>(StringComparer.Ordinal);
        private static float _last = float.NegativeInfinity;
        private static bool _publishFailed;

        /// <summary>Shows the text when the throttle allows it now.</summary>
        internal static void Show(string text)
        {
            if (!Admit(text, Time.unscaledTime)) return;
            try
            {
                ViewEventBus bus = ViewEventBus.Instance;
                if (bus != null && bus.OnShowQuickWarning != null) bus.OnShowQuickWarning.Publish(text);
            }
            catch (Exception e)
            {
                if (_publishFailed) return;
                _publishFailed = true;
                Plugin.Log.LogWarning("Ectype: interaction gate: the quick-warning channel failed (logged " +
                                      "once); warnings are not shown: " + e.Message);
            }
        }

        /// <summary>The throttle's decision at time <paramref name="now"/> (seconds), recording
        /// the show when admitted. Separate from <see cref="Show"/> so the dev self-test can drive
        /// it with its own clock.</summary>
        internal static bool Admit(string text, float now)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (now - _last < GapSeconds) return false;
            float shown;
            if (LastShown.TryGetValue(text, out shown) && now - shown < SameTextSeconds) return false;
            if (LastShown.Count > 256) LastShown.Clear();
            _last = now;
            LastShown[text] = now;
            return true;
        }

        /// <summary>Forgets every show (dev self-test).</summary>
        internal static void Reset()
        {
            LastShown.Clear();
            _last = float.NegativeInfinity;
        }
    }
}
