using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Game;
using CG.Ship.Modules;
using CG.Space;
using Client.Utils;
using Gameplay.Perks;
using Gameplay.Utilities;
using HarmonyLib;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// The framework-routed perk targets: every perk whose asset carries PerkBuffTarget value 4.
    /// The game's perk loader skips that value, so vanilla never applies these perks; their
    /// effects stay on the perk asset's own levels, where the tooltip already renders them from.
    /// What value 4 means for one perk is its entry in the scope table (PerkScopes): a ship set
    /// (the holder's crew ship, the ship they are aboard, every ship on their side, every ship on
    /// their seat's side) and a distribution. A perk with no entry is characterShip.
    ///
    /// This route applies them at the two moments vanilla applies every other target -
    /// ReApplyPerks and ResetPerks, which run on every client for every player - so every client
    /// carries every holder's contribution: per holder and per ship, one copy of the level's
    /// modifiers.
    ///
    /// Which ships. With one ship object in the room every set names that ship, as the stock
    /// targets do. With several, the game's player-ship getter can return a different ship on
    /// each client (a multi-ship mod points it at the ship the viewer is looking at), so each
    /// holder is resolved on its own from data every client shares (TopologyView), and the ship
    /// a holder is aboard from that holder's own answer (HolderConditions). A holder whose
    /// set is empty (spectating, not yet placed, crew ship dying, not seated) contributes nothing
    /// until it is not. Resolution is repeated four times a second, the frame after every topology
    /// change and every seat change, and only a holder whose ships changed is rebuilt.
    ///
    /// A perk with conditions applies only while they hold on the holder's condition mask, which
    /// the holder's own client decides and publishes (HolderConditions); a change of answer
    /// rebuilds that holder. A character perk with conditions is routed here too, onto the
    /// holder's own character collection.
    ///
    /// Each applied StatMod gets its own source object, never the player. Standing up removes
    /// every ship modifier whose source is the player (SittingInChair.AbilityStopped and
    /// OnDestroy), so a player-sourced modifier would vanish on the first stand. Nothing but this
    /// route ever holds a per-StatMod source, so nothing else removes one.
    ///
    /// The ship's collection does not reach every module. A ship module joins the ship's tree
    /// only through a build socket (BuildSocket.SetModule, whose socket parents itself to the
    /// ship in CarryablesSocket.Start) or by registering itself (Helm, CentralShipComputer,
    /// CloneTube). A core system nested in the ship prefab that does neither - the Fabricator -
    /// is the root of its own tree, and nothing applied to the ship reaches it. So a ship
    /// contribution also goes onto the root of every module collection that does not chain up
    /// to the ship's. When one of those roots later gains a parent, the parent replays its
    /// modifiers into it, so every holder is rebuilt at that moment to keep one copy.
    /// </summary>
    internal static class CharacterShipRoute
    {
        internal const PerkBuffTarget CharacterShip = (PerkBuffTarget)4;
        internal const string CharacterShipName = "characterShip";

        /// <summary>Removal key for one applied StatMod.</summary>
        private sealed class Source : IModifierSource
        {
            private readonly string _label;
            public Source(string label) { _label = label; }
            public override string ToString() { return _label; }
        }

        /// <summary>One collection a holder's contribution sits in: a ship's, with the ship's
        /// detached module roots, or the holder's own character's.</summary>
        private sealed class Placement
        {
            /// <summary>The ship, or the character, whose collection this is.</summary>
            public UnityEngine.Object Owner;
            public PlayerControlledShip Ship;
            public StatTagCollection Root;
            public readonly List<StatTagCollection> Detached = new List<StatTagCollection>();
            public readonly List<StatMod> Mods = new List<StatMod>();
        }

        /// <summary>One held perk this route applies.</summary>
        private struct Entry
        {
            public PerkBuff Perk;
            public PerkScope Scope;
            public List<StatMod> Level;
        }

        private sealed class Holder
        {
            public PlayerPerkLoader Loader;
            public CG.Game.Player.Player Player;
            /// <summary>Held perks this route applies, at the last rebuild.</summary>
            public int Routed;
            /// <summary>Held perks with conditions, and how many of them did not hold, at the
            /// last rebuild.</summary>
            public int Conditional, HeldOff;
            /// <summary>The holder's condition mask the last rebuild gated on.</summary>
            public int Mask = -1;
            public readonly List<Placement> Placed = new List<Placement>();
            /// <summary>What each ship set the held perks use named at the last rebuild.</summary>
            public readonly Dictionary<ShipSet, object[]> Resolved = new Dictionary<ShipSet, object[]>();
            public string LoggedState;
            public readonly Dictionary<ShipSet, string> LoggedShips = new Dictionary<ShipSet, string>();
        }

        private const float PassSeconds = 0.25f;

        private static readonly List<Holder> _holders = new List<Holder>();
        private static readonly HashSet<string> _reported = new HashSet<string>();
        private static readonly List<object> _scratch = new List<object>();
        private static ClientGame _boundGame;
        private static bool _busBound;
        private static bool _topologyBound;
        private static bool _passDue;
        private static float _nextPass;

        /// <summary>Rebuilds one holder's contribution from its current perk levels.</summary>
        public static void Reapply(PlayerPerkLoader loader)
        {
            if (loader == null) return;
            Bind();
            Holder h = Find(loader);
            if (h == null)
            {
                h = new Holder { Loader = loader, Player = loader.GetComponent<CG.Game.Player.Player>() };
                _holders.Add(h);
            }
            RemoveAll(h);

            List<Entry> held = Held(loader);
            h.Routed = held.Count;
            int actor = Topology.ActorOf(h.Player);
            h.Mask = HolderConditions.MaskOf(actor);
            h.Conditional = h.HeldOff = 0;
            // A perk whose conditions do not hold on the holder's mask is held off; the mask
            // changing rebuilds the holder.
            List<Entry> entries = new List<Entry>(held.Count);
            foreach (Entry e in held)
            {
                if (!e.Scope.When.Empty) h.Conditional++;
                if (e.Scope.When.Holds(h.Mask)) entries.Add(e);
                else h.HeldOff++;
            }
            if (entries.Count > 0)
            {
                foreach (Entry e in entries)
                    if (!h.Resolved.ContainsKey(e.Scope.Ships))
                        h.Resolved[e.Scope.Ships] = ResolveSet(h, e.Scope.Ships, actor);
                foreach (KeyValuePair<ShipSet, object[]> kv in h.Resolved) LogShips(h, kv.Key, kv.Value, actor);
                foreach (Entry e in entries) Place(h, e);
                foreach (Placement p in h.Placed) Apply(p);
            }
            LogState(h);
        }

        /// <summary>Takes one holder's contribution off; the perk loader's ResetPerks
        /// counterpart.</summary>
        public static void Remove(PlayerPerkLoader loader)
        {
            Holder h = Find(loader);
            if (h == null) return;
            RemoveAll(h);
            h.Routed = h.Conditional = h.HeldOff = 0;
            LogState(h);
        }

        private static Holder Find(PlayerPerkLoader loader)
        {
            for (int i = 0; i < _holders.Count; i++)
                if (ReferenceEquals(_holders[i].Loader, loader)) return _holders[i];
            return null;
        }

        /// <summary>A ship one of the holder's contributions sits on, or null while every one is
        /// pending, sits on the character, or the holder carries no routed perk.</summary>
        internal static PlayerControlledShip ShipOf(PlayerPerkLoader loader)
        {
            Holder h = Find(loader);
            if (h == null) return null;
            foreach (Placement p in h.Placed)
                if (p.Ship != null) return p.Ship;
            return null;
        }

        /// <summary>The routed perks the loader holds, each with its level's modifiers.</summary>
        private static List<Entry> Held(PlayerPerkLoader loader)
        {
            List<Entry> entries = new List<Entry>();
            // The ReApplyPerks prefix has already dropped unresolvable and suppressed entries
            // from this dictionary and clamped out-of-range levels, in place; the level check
            // below is the backstop if that prefix failed.
            Dictionary<GUIDUnion, int> levels = loader.PerkBuffLevels;
            PerkBuffContainer container = PerkBuffContainer.Instance;
            if (levels == null || container == null) return entries;
            foreach (KeyValuePair<GUIDUnion, int> kv in levels)
            {
                if (kv.Key.IsNull || kv.Value <= 0) continue;
                PerkBuffDef def;
                if (!container.TryGetByGuid(kv.Key, out def) || def == null) continue;
                PerkBuff perk = def.Asset;
                if (perk == null || perk.Target != CharacterShip) continue;
                PerkScope scope = PerkScopes.Of(kv.Key);
                if (perk.Levels == null || kv.Value > perk.Levels.Count ||
                    perk.Levels[kv.Value - 1] == null || perk.Levels[kv.Value - 1].Mods == null)
                {
                    if (_reported.Add(kv.Key.AsHex() + ":" + kv.Value))
                        Plugin.Log.LogError("Ectype: " + scope.Target + " perk '" + perk.name +
                                            "' held at level " + kv.Value + " has no such " +
                                            "level - skipped.");
                    continue;
                }
                entries.Add(new Entry { Perk = perk, Scope = scope, Level = perk.Levels[kv.Value - 1].Mods });
            }
            return entries;
        }

        /// <summary>The collections one ship set names for the holder: ships with a stat
        /// collection, or the character for <see cref="ShipSet.Character"/>.</summary>
        private static object[] ResolveSet(Holder h, ShipSet set, int actor)
        {
            if (set == ShipSet.Character)
                return h.Player != null && h.Player.Stats != null ? new object[] { h.Player } : new object[0];
            PerkScopes.Resolve(Topology.View, set, actor, HolderConditions.MaskOf(actor),
                               HolderConditions.AboardShipOf(actor), _scratch);
            List<object> ships = new List<object>(_scratch.Count);
            foreach (object o in _scratch)
            {
                PlayerControlledShip ship = o as PlayerControlledShip;
                if (ship != null && ship.Stats != null) ships.Add(ship);
            }
            return ships.ToArray();
        }

        /// <summary>One copy of the entry's level per collection its set names, each modifier
        /// with its own source and, for a split, its share of the amount.</summary>
        private static void Place(Holder h, Entry e)
        {
            object[] targets = h.Resolved[e.Scope.Ships];
            float factor = PerkScopes.SplitFactor(e.Scope.Share, targets.Length);
            foreach (object target in targets)
            {
                Placement p = PlacementFor(h, target);
                // Deep-copied and attributed exactly as the perk loader does for the stock
                // targets, so readouts name the perk as the origin.
                foreach (StatMod mod in e.Level.DeepCopy())
                {
                    if (mod == null || mod.Mod == null) continue;
                    mod.Mod.Source = new Source("Ectype " + e.Scope.Target + " " + e.Perk.name);
                    mod.Mod.InformationSource = e.Perk;
                    PerkScopes.Scale(mod, factor);
                    p.Mods.Add(mod);
                }
            }
        }

        private static Placement PlacementFor(Holder h, object target)
        {
            foreach (Placement existing in h.Placed)
                if (ReferenceEquals(existing.Owner, target)) return existing;
            Placement p = new Placement();
            PlayerControlledShip ship = target as PlayerControlledShip;
            if (ship != null) { p.Owner = ship; p.Ship = ship; p.Root = ship.Stats; }
            else { p.Owner = h.Player; p.Root = h.Player.Stats; }
            h.Placed.Add(p);
            return p;
        }

        private static void Apply(Placement p)
        {
            if (p.Mods.Count == 0 || p.Root == null) return;
            p.Root.ApplyModifiers(p.Mods);
            if (p.Ship == null) return;
            foreach (StatTagCollection root in DetachedRoots(p.Ship))
            {
                root.ApplyModifiers(p.Mods);
                p.Detached.Add(root);
            }
        }

        /// <summary>Verbose: "&lt;target&gt; - actor N → ship K (source)" whenever what a holder's
        /// ship set names changes.</summary>
        private static void LogShips(Holder h, ShipSet set, object[] ships, int actor)
        {
            if (!Plugin.Verbose || h.Player == null || set == ShipSet.Character) return;
            string line = "Ectype: " + PerkScopes.TargetOf(set) + " - actor " + actor + " → " +
                          ShipList(ships) + " (" + SourceOf(set, ships, actor) + ").";
            string last;
            if (h.LoggedShips.TryGetValue(set, out last) && last == line) return;
            h.LoggedShips[set] = line;
            Plugin.Log.LogInfo(line);
        }

        private static string ShipList(object[] ships)
        {
            if (ships.Length == 0) return "no ship";
            List<string> numbers = new List<string>(ships.Length);
            foreach (object o in ships) numbers.Add(Topology.ShipNumberOf(o as PlayerControlledShip).ToString());
            return (ships.Length == 1 ? "ship " : "ships ") + string.Join(", ", numbers.ToArray());
        }

        /// <summary>Which rule decided, for the verbose line.</summary>
        private static string SourceOf(ShipSet set, object[] ships, int actor)
        {
            switch (set)
            {
                case ShipSet.Crew:
                {
                    string source;
                    LiveTopologyView.Instance.CrewShip(actor, out source);
                    return source;
                }
                case ShipSet.Aboard:
                {
                    if (ships.Length > 0) return "aboard";
                    if (HolderConditions.MaskOf(actor) < 0) return "no answer from the holder yet";
                    if (!Topology.View.Multi) return "not on own ship";
                    int number = HolderConditions.AboardShipOf(actor);
                    if (number == 0) return "open space";
                    if (Topology.View.ShipByNumber(number) == null) return "ship " + number + " not alive";
                    return "aboard another side's ship";
                }
                case ShipSet.Force:
                    return ships.Length > 0 ? "holder's side" : "no living ship on the holder's side";
                case ShipSet.SeatForce:
                    if (Topology.View.SeatShip(actor) == null) return "not seated";
                    return ships.Length > 0 ? "seat's side" : "no living ship on the seat's side";
                default:
                    return "";
            }
        }

        /// <summary>Driven from EctypeDriver.Update: runs the pass four times a second, and on the
        /// frame after a topology change, a ship set or a seat change.</summary>
        internal static void Poll()
        {
            if (_holders.Count == 0) { _passDue = false; return; }
            BindTopology();
            float now = UnityEngine.Time.unscaledTime;
            if (!_passDue && now < _nextPass) return;
            _passDue = false;
            _nextPass = now + PassSeconds;
            Pass();
        }

        /// <summary>Asks for a pass on the next driver frame.</summary>
        internal static void RequestPass() { _passDue = true; }

        private static void BindTopology()
        {
            if (_topologyBound) return;
            _topologyBound = true;
            Topology.Changed += RequestPass;
        }

        /// <summary>Re-resolves every holder and rebuilds only those whose ships changed: a set
        /// that now names other ships, a pending set whose ship now exists, or a ship destroyed
        /// under a contribution.</summary>
        private static void Pass()
        {
            foreach (Holder h in _holders.ToArray())
            {
                if (h.Loader == null)
                {
                    RemoveAll(h);
                    _holders.Remove(h);
                    continue;
                }
                if (h.Routed == 0 || !Changed(h)) continue;
                try { Reapply(h.Loader); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: perk route rebuild for a ship change failed: " + e);
                }
            }
        }

        private static bool Changed(Holder h)
        {
            foreach (Placement p in h.Placed)
                if (!ReferenceEquals(p.Owner, null) && p.Owner == null) return true;
            int actor = Topology.ActorOf(h.Player);
            if (h.Conditional > 0 && HolderConditions.MaskOf(actor) != h.Mask) return true;
            foreach (KeyValuePair<ShipSet, object[]> kv in h.Resolved)
            {
                object[] now = ResolveSet(h, kv.Key, actor);
                if (now.Length != kv.Value.Length) return true;
                for (int i = 0; i < now.Length; i++)
                    if (!ReferenceEquals(now[i], kv.Value[i])) return true;
            }
            return false;
        }

        private static void RemoveAll(Holder h)
        {
            foreach (Placement p in h.Placed)
            {
                // A destroyed ship or character took its collection with it.
                if (p.Owner != null && p.Root != null)
                    foreach (StatMod mod in p.Mods)
                        p.Root.RemoveModifier(mod.Mod.Source);
                foreach (StatTagCollection root in p.Detached)
                {
                    if (!Alive(root)) continue;
                    foreach (StatMod mod in p.Mods)
                        root.RemoveModifier(mod.Mod.Source);
                }
            }
            h.Placed.Clear();
            h.Resolved.Clear();
        }

        /// <summary>False when the collection's Unity holder is destroyed: its change
        /// subscribers went with it.</summary>
        private static bool Alive(StatTagCollection col)
        {
            UnityEngine.Object holder = col.Holder as UnityEngine.Object;
            return ReferenceEquals(holder, null) || holder != null;
        }

        /// <summary>
        /// The distinct roots of the ship's module collections that do not chain up to the
        /// ship's own collection. Empty when every module is in the ship's tree.
        /// </summary>
        internal static List<StatTagCollection> DetachedRoots(PlayerControlledShip ship)
        {
            List<StatTagCollection> roots = new List<StatTagCollection>();
            if (ship == null || ship.Stats == null) return roots;
            List<CellModule> modules = ship.GetAllModules();
            if (modules == null) return roots;
            foreach (CellModule m in modules)
            {
                if (m == null || m.Stats == null) continue;
                StatTagCollection node = m.Stats;
                bool reached = false;
                for (int depth = 0; depth < 64; depth++)
                {
                    if (ReferenceEquals(node, ship.Stats)) { reached = true; break; }
                    if (node.ParentCollection == null) break;
                    node = node.ParentCollection;
                }
                if (!reached && !roots.Contains(node)) roots.Add(node);
            }
            return roots;
        }

        /// <summary>
        /// StatTagCollection.RegisterChildCollection has just replayed the new parent's
        /// modifiers into <paramref name="child"/>. When the child is a detached root carrying a
        /// contribution, that contribution may now sit in it twice; rebuilding every holder
        /// removes both copies by source and reapplies against the current tree shape.
        /// </summary>
        internal static void OnChildAttached(StatTagCollection child)
        {
            if (child == null) return;
            bool hit = false;
            for (int i = 0; i < _holders.Count && !hit; i++)
                foreach (Placement p in _holders[i].Placed)
                    if (p.Detached.Contains(child)) { hit = true; break; }
            if (!hit) return;
            foreach (Holder h in _holders.ToArray())
                if (h.Loader != null) Reapply(h.Loader);
        }

        /// <summary>Verbose: "perk route - actor N contributes M modifier(s) to K ship(s) ..."
        /// whenever a holder's totals change.</summary>
        private static void LogState(Holder h)
        {
            int mods = 0, ships = 0, detached = 0;
            bool character = false;
            foreach (Placement p in h.Placed)
            {
                mods += p.Mods.Count;
                if (p.Ship != null) ships++; else character = true;
                detached += p.Detached.Count;
            }
            bool pending = h.Routed > h.HeldOff && h.Placed.Count == 0;
            string state = mods + "/" + ships + "/" + character + "/" + detached + "/" + pending + "/" + h.HeldOff;
            if (state == h.LoggedState) return;
            h.LoggedState = state;
            if (!Plugin.Verbose) return;
            string who = "actor ?";
            try { if (h.Player != null) who = "actor " + h.Player.ActorId; }
            catch (Exception) { }
            string line = "Ectype: perk route - " + who + " contributes " + mods + " modifier(s) to " +
                          ships + " ship(s)" + (character ? " and the character" : "") +
                          (detached > 0 ? ", " + detached + " detached module tree(s)" : "") +
                          (pending ? ", waiting for a ship" : "") +
                          (h.HeldOff > 0 ? ", " + h.HeldOff + " perk(s) held off by conditions (" +
                           HolderConditions.Describe(h.Mask) + ")" : "") + ".";
            Plugin.Log.LogInfo(line);
        }

        /// <summary>
        /// Subscribes to the ship-change and player-removal events of the current ClientGame,
        /// once per game. Both are instance members, so a new game needs a new subscription.
        /// </summary>
        private static void Bind()
        {
            ClientGame game = ClientGame.Current;
            if (game == null) return;
            if (!ReferenceEquals(game, _boundGame))
            {
                if (!ReferenceEquals(_boundGame, null))
                {
                    try
                    {
                        _boundGame.OnPlayerShipSet -= RequestPass;
                        if (_busBound && _boundGame.ModelEventBus != null)
                            _boundGame.ModelEventBus.OnPlayerRemoved.Unsubscribe(OnPlayerRemoved);
                    }
                    catch (Exception) { }
                }
                // ClientGame.OnPlayerShipSet: a ship finished spawning. Each ship's start raises
                // it, so a mission with several ships raises it several times; the pass these
                // requests trigger runs once, on the next frame.
                game.OnPlayerShipSet += RequestPass;
                _boundGame = game;
                _busBound = false;
                // The previous game's players are gone with it.
                _holders.RemoveAll(x => x.Loader == null);
            }
            if (!_busBound && game.ModelEventBus != null && game.ModelEventBus.OnPlayerRemoved != null)
            {
                game.ModelEventBus.OnPlayerRemoved.Subscribe(OnPlayerRemoved);
                _busBound = true;
            }
        }

        /// <summary>ModelEventBus.OnPlayerRemoved: the leaving player's contribution comes off.
        /// Published after the character's destroy is queued, so match by reference rather than
        /// through the character.</summary>
        private static void OnPlayerRemoved(CG.Game.Player.Player player)
        {
            try
            {
                for (int i = _holders.Count - 1; i >= 0; i--)
                {
                    Holder h = _holders[i];
                    if (!ReferenceEquals(h.Player, player) && h.Loader != null) continue;
                    RemoveAll(h);
                    h.Routed = h.Conditional = h.HeldOff = 0;
                    LogState(h);
                    _holders.RemoveAt(i);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: perk route player-removal handling failed: " + e);
            }
        }

        // ------------------------------------------------------------------ seat hooks

        /// <summary>
        /// Postfixes on the seat ability's start and stop, so a seatFleet holder is rebuilt the
        /// frame after they sit or stand rather than at the next pass. The seat ability runs on
        /// every client's copy of the character. Patched by name on its own: a miss leaves the
        /// four-a-second pass to notice seat changes.
        /// </summary>
        internal static void ApplyPatches(Harmony harmony)
        {
            MethodInfo postfix = AccessTools.Method(typeof(CharacterShipRoute), nameof(SeatChanged));
            foreach (string name in new[] { "AbilityStarted", "AbilityStopped" })
            {
                MethodInfo target = AccessTools.DeclaredMethod(typeof(SittingInChair), name);
                if (target == null)
                {
                    Plugin.Log.LogWarning("Ectype: SittingInChair." + name + " not found (game update?); " +
                                          "seatFleet perks follow seat changes at the next pass instead.");
                    continue;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
        }

        private static void SeatChanged()
        {
            if (_holders.Count > 0) _passDue = true;
        }
    }

    /// <summary>
    /// Postfix: applies the holder's routed effects after vanilla has applied the stock
    /// targets. Finalizer: a throw inside ReApplyPerks skips the postfix but has already run
    /// ResetPerks, which took the contribution off - the finalizer puts it back. The postfix
    /// also reports framework effects that reach no stat in their target's tree.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPerkLoader), "ReApplyPerks")]
    internal static class Patch_PlayerPerkLoader_ReApplyPerks_CharacterShip
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerPerkLoader __instance)
        {
            Run(__instance);
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try { TargetReach.Check(__instance); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: target reach check failed: " + e); }
        }

        [HarmonyFinalizer]
        private static void Finalizer(PlayerPerkLoader __instance, Exception __exception)
        {
            if (__exception != null) Run(__instance);
        }

        private static void Run(PlayerPerkLoader loader)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try { CharacterShipRoute.Reapply(loader); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: perk route reapply failed: " + e); }
        }
    }

    /// <summary>Postfix: a detached module root that gains a parent has just had the parent's
    /// modifiers replayed into it; every routed contribution is rebuilt so it sits there
    /// once.</summary>
    [HarmonyPatch(typeof(StatTagCollection), "RegisterChildCollection")]
    internal static class Patch_StatTagCollection_RegisterChildCollection_CharacterShip
    {
        [HarmonyPostfix]
        private static void Postfix(StatTagCollection collection)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try { CharacterShipRoute.OnChildAttached(collection); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: perk route re-parent handling failed: " + e); }
        }
    }

    /// <summary>Postfix: ResetPerks removes the player-sourced modifiers and the parked seat
    /// lists; this removes the holder's routed modifiers alongside them.</summary>
    [HarmonyPatch(typeof(PlayerPerkLoader), "ResetPerks")]
    internal static class Patch_PlayerPerkLoader_ResetPerks_CharacterShip
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerPerkLoader __instance)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try { CharacterShipRoute.Remove(__instance); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: perk route reset failed: " + e); }
        }
    }
}
