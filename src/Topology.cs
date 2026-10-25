using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

using BepInEx.Bootstrap;
using CG.Game;
using CG.Objects;
using CG.Ship.Hull;
using CG.Space;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Which player ships exist, which ship each player crews, and who is on which side, answered
    /// the same way on every client.
    ///
    /// The game has one player ship and one getter for it (ClientGame.PlayerShip). A mod that puts
    /// several player ships in a room can make that getter return the ship the viewing client is
    /// looking at, so two clients get two different answers. Anything that changes simulation
    /// state on every client must therefore never read the getter for another player. This class
    /// answers from data every client agrees on: the ship objects themselves (a registry filled
    /// from each ship's Awake and emptied at its OnDestroy, with death marked from the death RPC
    /// that reaches every client), the characters' platforms and factions, and, when Void Crew
    /// Fleet is installed, its public API for crew membership.
    ///
    /// In a room with one ship object every answer is that ship, so behaviour matches the game's
    /// own.
    /// </summary>
    public static class Topology
    {
        /// <summary>The plugin GUID of Void Crew Fleet, the multi-ship mod this binds to by reflection.</summary>
        public const string FleetPluginGuid = "com.captainchunck.voidcrew.fleet";

        /// <summary>The room property Fleet publishes its ships in: an int[] of root PhotonView ids by
        /// slot, ship 1 first, written once when the ships spawn. A lost ship keeps its index.</summary>
        private const string FleetShipsKey = "VCF_Ships";
        private const int MaxFleetShips = 6;
        private const float PollSeconds = 0.5f;
        private const float LogSettleSeconds = 2f;

        private static readonly List<PlayerControlledShip> _all = new List<PlayerControlledShip>();
        private static readonly List<PlayerControlledShip> _living = new List<PlayerControlledShip>();
        private static readonly ReadOnlyCollection<PlayerControlledShip> _livingView =
            new ReadOnlyCollection<PlayerControlledShip>(_living);
        /// <summary>Every root view id seen this mission, ascending: the fallback ship numbering.
        /// A lost ship stays in it so the others keep their numbers.</summary>
        private static readonly List<int> _seenViews = new List<int>();

        /// <summary>A change Changed has not announced yet.</summary>
        private static bool _dirty = true;
        /// <summary>The living list needs a rebuild.</summary>
        private static bool _stale = true;
        private static int _stamp;
        private static float _nextPoll;
        private static float _logDue = -1f;
        private static string _lastLogged;

        private static object _fleetShipsRaw;
        private static bool _fleetShipsSettled;
        private static int[] _fleetShips;
        private static bool _warnedFleetShips;

        /// <summary>Raised on the main thread after the ship registry, a ship's death, Fleet's ships or
        /// teams, or Fleet's published ship list changed. Subscribers run in their own try/catch.</summary>
        public static event Action Changed;

        /// <summary>Advances whenever any answer here may have changed. Compare it to a stored value to
        /// re-resolve only on change.</summary>
        public static int Stamp { get { return _stamp; } }

        /// <summary>Every living player ship (not dying), ordered by ship number. Empty outside a
        /// mission.</summary>
        public static IReadOnlyList<PlayerControlledShip> Ships
        {
            get { Refresh(); return _livingView; }
        }

        /// <summary>True while more than one player ship object exists, dying ones included. Only then
        /// can the game's player-ship getter differ between clients.</summary>
        public static bool Multi
        {
            get { Prune(); return _all.Count > 1; }
        }

        /// <summary>"fleet" when Void Crew Fleet's API is bound, "single" otherwise.</summary>
        public static string Provider
        {
            get { return Fleet.Bound ? "fleet" : "single"; }
        }

        /// <summary>
        /// The ship a player crews, identical on every client, or null while none can be named (a
        /// spectator, a player not yet placed, a crew whose ship is dying).
        ///
        /// Resolution, first answer wins: the only ship object when there is one; the only living
        /// ship; the ship Fleet's API names for the player; the ship the player's character is
        /// aboard (its zoned platform, else its platform) when that ship's faction equals the
        /// character's, so a boarder never resolves to the enemy's ship.
        /// </summary>
        public static PlayerControlledShip CrewShipOf(Photon.Realtime.Player player)
        {
            if (player == null) return null;
            string source;
            return ResolveCrewShip(player.ActorNumber, CharacterOf(player.ActorNumber), out source);
        }

        /// <summary>The crew ship of a character's player; see <see cref="CrewShipOf(Photon.Realtime.Player)"/>.</summary>
        public static PlayerControlledShip CrewShipOf(CG.Game.Player.Player character)
        {
            if (character == null) return null;
            string source;
            return ResolveCrewShip(ActorOf(character), character, out source);
        }

        /// <summary>The ship whose rooms or inertia bubble hold the player's character, or null (no
        /// character, open space). The game's own reading: the character's zoned platform, else its
        /// platform.</summary>
        public static PlayerControlledShip AboardShipOf(Photon.Realtime.Player player)
        {
            return player == null ? null : AboardShipOf(CharacterOf(player.ActorNumber));
        }

        /// <summary>The ship a character is aboard; see <see cref="AboardShipOf(Photon.Realtime.Player)"/>.</summary>
        public static PlayerControlledShip AboardShipOf(CG.Game.Player.Player character)
        {
            if (character == null) return null;
            try
            {
                Component platform = character.ZonedPlatform;
                if (platform == null) platform = character.Platform;
                return platform != null ? platform.GetComponentInParent<PlayerControlledShip>() : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The player ship a part belongs to, or null. A part under the ship's hierarchy answers
        /// directly. The hull is unparented from its ship at spawn and keeps a reference back to
        /// it. A carryable answers through the platform it sits on or is carried on.
        /// </summary>
        public static PlayerControlledShip ShipOfPart(Component part)
        {
            if (part == null) return null;
            try
            {
                PlayerControlledShip ship = part.GetComponentInParent<PlayerControlledShip>();
                if (ship != null) return ship;
                ShipHull hull = part.GetComponentInParent<ShipHull>();
                if (hull != null && hull.Ship != null) return hull.Ship;
                CarryableObject carryable = part.GetComponentInParent<CarryableObject>();
                if (carryable != null && carryable.ParentPlatform != null)
                    return carryable.ParentPlatform.GetComponentInParent<PlayerControlledShip>();
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// A ship's number, 1 to 6, or 0 when unknown. Under Fleet it is the ship's index in the
        /// host's published list plus one. Otherwise it is the ship's rank by root PhotonView id
        /// among every ship seen this mission, so a lost ship keeps its number and the others keep
        /// theirs. In a one-ship room it is 1.
        /// </summary>
        public static int ShipNumberOf(PlayerControlledShip ship)
        {
            if (ship == null) return 0;
            int view = ViewId(ship);
            if (view == 0) return 0;
            int[] published = _fleetShips;
            if (published != null)
            {
                int i = Array.IndexOf(published, view);
                if (i >= 0) return i + 1;
            }
            Prune();
            int rank = _seenViews.BinarySearch(view);
            return rank >= 0 ? rank + 1 : 0;
        }

        /// <summary>The living ship with this number, or null.</summary>
        public static PlayerControlledShip ShipByNumber(int number)
        {
            if (number < 1) return null;
            IReadOnlyList<PlayerControlledShip> ships = Ships;
            for (int i = 0; i < ships.Count; i++)
                if (ShipNumberOf(ships[i]) == number) return ships[i];
            return null;
        }

        /// <summary>The side a ship is on: its game faction. Two ships with the same value are on one
        /// side. -1 for null.</summary>
        public static int ForceOf(PlayerControlledShip ship)
        {
            return ship == null ? -1 : ship.Faction;
        }

        /// <summary>The side a player is on: their character's faction; without a character (a
        /// spectator), the faction of their crew ship, else under a Fleet team match the faction of
        /// a living ship of their team; -1 when none of these is known.</summary>
        public static int ForceOf(Photon.Realtime.Player player)
        {
            return player == null ? -1 : ForceOfActor(player.ActorNumber);
        }

        /// <summary>The side a character is on: its faction. -1 for null.</summary>
        public static int ForceOf(CG.Game.Player.Player character)
        {
            return character == null ? -1 : character.Faction;
        }

        /// <summary>
        /// True when a and b are on different sides. Each may be a player ship, a character, a
        /// Photon player, any space object, or a component of a ship (resolved through
        /// <see cref="ShipOfPart"/>). Two space objects are compared with the game's own
        /// different-faction check, which a multi-ship mod keeps team-correct. False when either
        /// side is unknown.
        /// </summary>
        public static bool Hostile(object a, object b)
        {
            try
            {
                OrbitObject oa = a as OrbitObject, ob = b as OrbitObject;
                if (oa != null && ob != null) return oa.IsObjectFromDifferentFaction(ob);
                int fa = ForceOfAny(a), fb = ForceOfAny(b);
                return fa >= 0 && fb >= 0 && fa != fb;
            }
            catch (Exception) { return false; }
        }

        // ---- internals ----------------------------------------------------------------------

        internal static PlayerControlledShip ResolveCrewShip(int actor, CG.Game.Player.Player character,
                                                             out string source)
        {
            Prune();
            if (_all.Count <= 1)
            {
                source = "single";
                if (_all.Count == 1) return _all[0];
                // Registry empty (patch missed, or before the first Awake): with no second ship
                // object the getter is the room's one ship.
                ClientGame game = ClientGame.Current;
                return game != null ? game.PlayerShip : null;
            }
            IReadOnlyList<PlayerControlledShip> living = Ships;
            if (living.Count == 1) { source = "only living ship"; return living[0]; }
            if (living.Count == 0) { source = "no living ship"; return null; }

            int number = Fleet.ShipOfPlayer(actor);
            if (number > 0)
            {
                PlayerControlledShip crew = ShipByNumber(number);
                if (crew != null) { source = "fleet crew"; return crew; }
            }

            PlayerControlledShip aboard = AboardShipOf(character);
            if (aboard != null && !aboard.StartedDeath && character != null && aboard.Faction == character.Faction)
            {
                source = "aboard, same faction";
                return aboard;
            }
            source = number > 0 ? "fleet ship " + number + " not found" : "pending";
            return null;
        }

        internal static int ActorOf(CG.Game.Player.Player character)
        {
            try { return character != null ? character.ActorId : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>The live character of an actor, or null. Skips destroyed entries the game's
        /// list can hold while a match ends.</summary>
        internal static CG.Game.Player.Player CharacterOf(int actor)
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return null;
            foreach (CG.Game.Player.Player p in game.Players)
            {
                if (p == null) continue;
                if (ActorOf(p) == actor) return p;
            }
            return null;
        }

        private static int ForceOfActor(int actor)
        {
            CG.Game.Player.Player character = CharacterOf(actor);
            if (character != null) return character.Faction;
            string source;
            PlayerControlledShip crew = ResolveCrewShip(actor, null, out source);
            if (crew != null) return crew.Faction;
            int team = Fleet.TeamOf(actor);
            if (team < 0) return -1;
            IReadOnlyList<PlayerControlledShip> ships = Ships;
            for (int i = 0; i < ships.Count; i++)
                if (Fleet.TeamOfShip(ShipNumberOf(ships[i])) == team) return ships[i].Faction;
            return -1;
        }

        private static int ForceOfAny(object o)
        {
            if (o == null) return -1;
            OrbitObject orbit = o as OrbitObject;
            if (orbit != null) return orbit.Faction;
            Photon.Realtime.Player photon = o as Photon.Realtime.Player;
            if (photon != null) return ForceOfActor(photon.ActorNumber);
            Component c = o as Component;
            if (c != null)
            {
                PlayerControlledShip ship = ShipOfPart(c);
                if (ship != null) return ship.Faction;
                OrbitObject parent = c.GetComponentInParent<OrbitObject>();
                if (parent != null) return parent.Faction;
            }
            return -1;
        }

        private static int ViewId(PlayerControlledShip ship)
        {
            try { return ship != null && ship.photonView != null ? ship.photonView.ViewID : 0; }
            catch (Exception) { return 0; }
        }

        internal static void OnShipAwake(PlayerControlledShip ship)
        {
            if (ship == null || _all.Contains(ship)) return;
            _all.Add(ship);
            MarkDirty();
        }

        internal static void OnShipDestroyed(PlayerControlledShip ship)
        {
            if (_all.Remove(ship)) MarkDirty();
        }

        internal static void OnShipDeathBegan(PlayerControlledShip ship)
        {
            MarkDirty();
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: topology: ship " + ShipNumberOf(ship) + " started dying.");
        }

        /// <summary>Drops destroyed ships. A ship's view id is assigned after Awake, so the
        /// fallback numbering picks it up here rather than at registration.</summary>
        private static void Prune()
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                if (_all[i] == null) { _all.RemoveAt(i); MarkDirty(); }
            }
            if (_all.Count == 0)
            {
                if (_seenViews.Count > 0) { _seenViews.Clear(); MarkDirty(); }
                return;
            }
            for (int i = 0; i < _all.Count; i++)
            {
                int view = ViewId(_all[i]);
                if (view == 0) continue;
                int at = _seenViews.BinarySearch(view);
                if (at < 0) { _seenViews.Insert(~at, view); MarkDirty(); }
            }
        }

        private static void Refresh()
        {
            Prune();
            if (!_stale && _living.Count == CountLiving()) return;
            _stale = false;
            _living.Clear();
            foreach (PlayerControlledShip s in _all)
                if (s != null && !s.StartedDeath) _living.Add(s);
            _living.Sort((x, y) =>
            {
                int nx = ShipNumberOf(x), ny = ShipNumberOf(y);
                if (nx != ny) return nx.CompareTo(ny);
                return ViewId(x).CompareTo(ViewId(y));
            });
        }

        private static int CountLiving()
        {
            int n = 0;
            foreach (PlayerControlledShip s in _all) if (s != null && !s.StartedDeath) n++;
            return n;
        }

        /// <summary>
        /// Fleet's published ship list when it passes its shape check, else null. Shape: an int[]
        /// of at most six entries, each 0 (no ship in that slot), a view id that resolves to a
        /// player ship, or a view id that resolves to nothing (a lost ship keeps its index); at
        /// least one entry must resolve to a player ship. A failed check logs once and the ship
        /// numbers come from view-id rank instead. Read at 2 Hz; the room replaces the value object
        /// on every write, so an unchanged reference needs no second check.
        /// </summary>
        private static void RecheckFleetShips()
        {
            object raw = null;
            try
            {
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                    PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(FleetShipsKey, out raw);
            }
            catch (Exception) { raw = null; }
            if (ReferenceEquals(raw, _fleetShipsRaw) && _fleetShipsSettled) return;
            _fleetShipsRaw = raw;
            _fleetShipsSettled = true;

            int[] before = _fleetShips;
            _fleetShips = CheckFleetShips(raw);
            if (!ReferenceEquals(before, _fleetShips)) MarkDirty();
        }

        private static int[] CheckFleetShips(object raw)
        {
            if (raw == null) return null;
            int[] views = raw as int[];
            string why = null;
            if (views == null) why = "not an int[] (" + raw.GetType().Name + ")";
            else if (views.Length == 0 || views.Length > MaxFleetShips) why = views.Length + " entries";
            else
            {
                int resolved = 0;
                for (int i = 0; i < views.Length && why == null; i++)
                {
                    if (views[i] == 0) continue;
                    PhotonView pv = PhotonView.Find(views[i]);
                    if (pv == null) continue;
                    if (pv.GetComponent<PlayerControlledShip>() == null)
                        why = "entry " + i + " (view " + views[i] + ") is not a player ship";
                    else resolved++;
                }
                // Before the ships have spawned on this client nothing resolves yet; that is not
                // a shape failure, so retry at the next poll.
                if (why == null && resolved == 0) { _fleetShipsSettled = false; return null; }
            }
            if (why != null)
            {
                if (!_warnedFleetShips)
                {
                    _warnedFleetShips = true;
                    Plugin.Log.LogWarning("Ectype: topology: Fleet's " + FleetShipsKey + " room property " +
                                          "failed its shape check (" + why + "); ship numbers come from " +
                                          "view-id rank instead.");
                }
                return null;
            }
            return views;
        }

        /// <summary>Driven from EctypeDriver.Update: raises Changed the frame after a registry
        /// change, and at 2 Hz rechecks what has no event of its own.</summary>
        internal static void Poll()
        {
            float now = Time.unscaledTime;
            if (now >= _nextPoll)
            {
                _nextPoll = now + PollSeconds;
                Fleet.EnsureBound();
                Prune();
                RecheckFleetShips();
                if (CountLiving() != _living.Count) MarkDirty();
            }
            if (_dirty)
            {
                _dirty = false;
                Refresh();
                _stamp++;
                _logDue = now + LogSettleSeconds;
                RaiseChanged();
            }
            if (_logDue >= 0f && now >= _logDue)
            {
                _logDue = -1f;
                LogSummary();
            }
        }

        /// <summary>The settled ship count, once per change: "Ectype: topology: N ship(s),
        /// provider=X". A vanilla mission logs 1 ship and provider=single.</summary>
        private static void LogSummary()
        {
            Prune();
            if (_all.Count == 0) { _lastLogged = null; return; }
            int living = CountLiving();
            string line = "Ectype: topology: " + living + " ship(s), provider=" + Provider +
                          (_all.Count > living ? ", " + (_all.Count - living) + " dying" : "") +
                          (Fleet.Bound ? ", numbers from " + (_fleetShips != null ? FleetShipsKey : "view-id rank") : "") +
                          ".";
            if (line == _lastLogged) return;
            _lastLogged = line;
            Plugin.Log.LogInfo(line);
        }

        internal static void MarkDirty() { _dirty = true; _stale = true; }

        private static void RaiseChanged()
        {
            Action handlers = Changed;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: Topology.Changed subscriber " +
                                        (d.Method.DeclaringType != null ? d.Method.DeclaringType.FullName : "?") +
                                        "." + d.Method.Name + " threw: " + e);
                }
            }
        }

        /// <summary>
        /// Void Crew Fleet's public API, bound by reflection at first use so the framework needs no
        /// reference to it. Each member is probed by name and signature; a missing one is logged
        /// once and its answer falls back to game data. Every call is wrapped so a throw reads as
        /// "unknown".
        /// </summary>
        private static class Fleet
        {
            private static bool _tried;
            private static float _nextTry;
            private static Func<int, int> _shipOfPlayer;
            private static Func<int, int> _teamOf;
            private static Func<int, int> _teamOfShip;

            internal static bool Bound { get { EnsureBound(); return _shipOfPlayer != null; } }

            internal static int ShipOfPlayer(int actor) { return Call(_shipOfPlayer, actor, 0); }
            internal static int TeamOf(int actor) { return Call(_teamOf, actor, -1); }
            internal static int TeamOfShip(int ship) { return ship < 1 ? -1 : Call(_teamOfShip, ship, -1); }

            private static int Call(Func<int, int> f, int arg, int fallback)
            {
                EnsureBound();
                if (f == null) return fallback;
                try { return f(arg); }
                catch (Exception) { return fallback; }
            }

            /// <summary>One attempt once BepInEx has loaded every plugin; until then, retried every
            /// few seconds so a call during start-up never binds against a half-loaded list.</summary>
            internal static void EnsureBound()
            {
                if (_tried) return;
                if (Time.unscaledTime < _nextTry) return;
                _nextTry = Time.unscaledTime + 5f;
                BepInEx.PluginInfo info;
                Dictionary<string, BepInEx.PluginInfo> infos = Chainloader.PluginInfos;
                if (infos == null || !infos.TryGetValue(FleetPluginGuid, out info))
                {
                    // Chainloader fills the table plugin by plugin; a plugin loaded after this one
                    // is absent until it has loaded. The driver runs after every plugin's Awake.
                    if (Time.frameCount > 1) _tried = true;
                    return;
                }
                _tried = true;
                try { Bind(info); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Ectype: topology: binding Void Crew Fleet's API failed; " +
                                          "answers come from game data alone: " + e.Message);
                }
            }

            private static void Bind(BepInEx.PluginInfo info)
            {
                Type api = null;
                if (info.Instance != null) api = info.Instance.GetType().Assembly.GetType("VoidCrewFleet.FleetApi", false);
                if (api == null)
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { api = a.GetType("VoidCrewFleet.FleetApi", false); }
                        catch (Exception) { }
                        if (api != null) break;
                    }
                if (api == null)
                {
                    Plugin.Log.LogWarning("Ectype: topology: Void Crew Fleet " + info.Metadata.Version +
                                          " is loaded but has no VoidCrewFleet.FleetApi; answers come " +
                                          "from game data alone.");
                    return;
                }

                int apiVersion = -1;
                FieldInfo ver = api.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (ver != null && ver.FieldType == typeof(int)) apiVersion = (int)ver.GetValue(null);

                List<string> missing = new List<string>();
                _shipOfPlayer = IntToInt(api, "ShipOfPlayer", missing);
                _teamOf = IntToInt(api, "TeamOf", missing);
                _teamOfShip = IntToInt(api, "TeamOfShip", missing);
                Subscribe(api, "ShipsChanged", missing);
                Subscribe(api, "TeamsChanged", missing);

                Plugin.Log.LogInfo("Ectype: topology: Void Crew Fleet " + info.Metadata.Version + " bound, API v" +
                                   (apiVersion >= 0 ? apiVersion.ToString() : "?") +
                                   (missing.Count == 0 ? "." : "; missing " + string.Join(", ", missing.ToArray()) +
                                    ", answered from game data instead."));
                MarkDirty();
            }

            private static Func<int, int> IntToInt(Type api, string name, List<string> missing)
            {
                MethodInfo m = api.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null,
                                             new[] { typeof(int) }, null);
                if (m == null || m.ReturnType != typeof(int))
                {
                    missing.Add("int " + name + "(int)");
                    return null;
                }
                try { return (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), m); }
                catch (Exception) { missing.Add("int " + name + "(int)"); return null; }
            }

            private static void Subscribe(Type api, string name, List<string> missing)
            {
                EventInfo ev = api.GetEvent(name, BindingFlags.Public | BindingFlags.Static);
                if (ev == null || ev.EventHandlerType != typeof(Action))
                {
                    missing.Add("event Action " + name);
                    return;
                }
                try { ev.AddEventHandler(null, (Action)OnFleetChanged); }
                catch (Exception) { missing.Add("event Action " + name); }
            }

            private static void OnFleetChanged() { MarkDirty(); }
        }
    }

    /// <summary>Postfix: Awake is where the ship makes itself the game's player ship, on every
    /// client. The registry learns of it here.</summary>
    [HarmonyPatch(typeof(PlayerControlledShip), "Awake")]
    internal static class PlayerControlledShip_Awake_Topology
    {
        private static void Postfix(PlayerControlledShip __instance)
        {
            if (!Plugin.PatchesApplied) return;
            try { Topology.OnShipAwake(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: topology ship registration failed: " + e); }
        }
    }

    /// <summary>Prefix: the ship leaves the registry before its components tear down.</summary>
    [HarmonyPatch(typeof(PlayerControlledShip), "OnDestroy")]
    internal static class PlayerControlledShip_OnDestroy_Topology
    {
        private static void Prefix(PlayerControlledShip __instance)
        {
            if (!Plugin.PatchesApplied) return;
            try { Topology.OnShipDestroyed(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: topology ship removal failed: " + e); }
        }
    }

    /// <summary>Postfix: the death-began RPC reaches every client and sets StartedDeath.</summary>
    [HarmonyPatch(typeof(PlayerControlledShip), "NetPublishShipDeathBegan")]
    internal static class PlayerControlledShip_NetPublishShipDeathBegan_Topology
    {
        private static void Postfix(PlayerControlledShip __instance)
        {
            if (!Plugin.PatchesApplied) return;
            try { Topology.OnShipDeathBegan(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: topology ship death handling failed: " + e); }
        }
    }
}
