using System;
using System.Collections.Generic;

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
    /// The characterShip perk target, PerkBuffTarget value 4: a perk's effects land on the
    /// holder's ship's stat collection for every player holding it, seated or not.
    ///
    /// Which ship. With one ship object in the room it is that ship. With several, the game's
    /// player-ship getter can return a different ship on each client (a multi-ship mod points it
    /// at the ship the viewer is looking at), so each holder is resolved on its own through
    /// Topology.CrewShipOf, from data every client shares: the holder's crew ship, else the ship
    /// the holder is aboard when it is of the holder's own faction. A holder with no answer
    /// (spectating, not yet placed, crew ship dying) stays pending. Resolution is repeated twice a
    /// second and on every topology change, and only a holder whose ship changed is rebuilt.
    ///
    /// The effects live on the perk asset's own levels, where the tooltip already renders
    /// them from; ReApplyPerks's target switch has no case for 4, so vanilla never applies
    /// them. This route applies them at the two moments vanilla applies every other target -
    /// ReApplyPerks and ResetPerks, which run on every client for every player - so every
    /// client carries every holder's contribution, one contribution per holder.
    ///
    /// Each applied StatMod gets its own source object, never the player. Standing up removes
    /// every ship modifier whose source is the player (SittingInChair.AbilityStopped and
    /// OnDestroy), so a player-sourced characterShip modifier would vanish on the first stand.
    /// Nothing but this route ever holds a per-StatMod source, so nothing else removes one.
    ///
    /// The ship's collection does not reach every module. A ship module joins the ship's tree
    /// only through a build socket (BuildSocket.SetModule, whose socket parents itself to the
    /// ship in CarryablesSocket.Start) or by registering itself (Helm, CentralShipComputer,
    /// CloneTube). A core system nested in the ship prefab that does neither - the Fabricator -
    /// is the root of its own tree, and nothing applied to the ship reaches it. So the
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

        private sealed class Holder
        {
            public PlayerPerkLoader Loader;
            public CG.Game.Player.Player Player;
            /// <summary>The collection Applied sits in; null while no ship exists.</summary>
            public PlayerControlledShip Ship;
            /// <summary>Detached module roots Applied also sits in.</summary>
            public readonly List<StatTagCollection> Detached = new List<StatTagCollection>();
            public readonly List<StatMod> Applied = new List<StatMod>();
            public string LoggedState;
            public string LoggedShip;
        }

        private const float PassSeconds = 0.5f;

        private static readonly List<Holder> _holders = new List<Holder>();
        private static readonly HashSet<string> _reported = new HashSet<string>();
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
            RemoveFromShip(h);

            // The ReApplyPerks prefix has already dropped unresolvable and suppressed entries
            // from this dictionary and clamped out-of-range levels, in place; the level check
            // below is the backstop if that prefix failed.
            Dictionary<GUIDUnion, int> levels = loader.PerkBuffLevels;
            PerkBuffContainer container = PerkBuffContainer.Instance;
            if (levels != null && container != null)
            {
                foreach (KeyValuePair<GUIDUnion, int> kv in levels)
                {
                    if (kv.Key.IsNull || kv.Value <= 0) continue;
                    PerkBuffDef def;
                    if (!container.TryGetByGuid(kv.Key, out def) || def == null) continue;
                    PerkBuff perk = def.Asset;
                    if (perk == null || perk.Target != CharacterShip) continue;
                    if (perk.Levels == null || kv.Value > perk.Levels.Count ||
                        perk.Levels[kv.Value - 1] == null || perk.Levels[kv.Value - 1].Mods == null)
                    {
                        if (_reported.Add(kv.Key.AsHex() + ":" + kv.Value))
                            Plugin.Log.LogError("Ectype: characterShip perk '" + perk.name +
                                                "' held at level " + kv.Value + " has no such " +
                                                "level - skipped.");
                        continue;
                    }
                    // Deep-copied and attributed exactly as the perk loader does for the other
                    // three targets, so readouts name the perk as the origin.
                    foreach (StatMod mod in perk.Levels[kv.Value - 1].Mods.DeepCopy())
                    {
                        if (mod == null || mod.Mod == null) continue;
                        mod.Mod.Source = new Source("Ectype characterShip " + perk.name);
                        mod.Mod.InformationSource = perk;
                        h.Applied.Add(mod);
                    }
                }
            }

            string source;
            PlayerControlledShip ship = Resolve(h, out source);
            if (h.Applied.Count > 0) LogShip(h, ship, source);
            if (h.Applied.Count > 0 && ship != null && ship.Stats != null)
            {
                ship.Stats.ApplyModifiers(h.Applied);
                h.Ship = ship;
                foreach (StatTagCollection root in DetachedRoots(ship))
                {
                    root.ApplyModifiers(h.Applied);
                    h.Detached.Add(root);
                }
            }
            LogState(h);
        }

        /// <summary>Takes one holder's contribution off the ship; the perk loader's
        /// ResetPerks counterpart.</summary>
        public static void Remove(PlayerPerkLoader loader)
        {
            Holder h = Find(loader);
            if (h == null) return;
            RemoveFromShip(h);
            LogState(h);
        }

        private static Holder Find(PlayerPerkLoader loader)
        {
            for (int i = 0; i < _holders.Count; i++)
                if (ReferenceEquals(_holders[i].Loader, loader)) return _holders[i];
            return null;
        }

        /// <summary>The ship a holder's contribution sits on, or null while it is pending or the
        /// holder carries no characterShip perk.</summary>
        internal static PlayerControlledShip ShipOf(PlayerPerkLoader loader)
        {
            Holder h = Find(loader);
            return h != null ? h.Ship : null;
        }

        /// <summary>The holder's ship. The player-ship getter is read only while one ship object
        /// exists, where it is the same on every client.</summary>
        private static PlayerControlledShip Resolve(Holder h, out string source)
        {
            if (!Topology.Multi)
            {
                source = "single";
                return ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
            }
            return Topology.ResolveCrewShip(Topology.ActorOf(h.Player), h.Player, out source);
        }

        /// <summary>Verbose: "actor N → ship K (source)" whenever a holder's resolution changes.</summary>
        private static void LogShip(Holder h, PlayerControlledShip ship, string source)
        {
            if (!Plugin.Verbose || h.Player == null) return;
            string line = "Ectype: characterShip - actor " + Topology.ActorOf(h.Player) + " → " +
                          (ship != null ? "ship " + Topology.ShipNumberOf(ship) : "no ship") +
                          " (" + source + ").";
            if (line == h.LoggedShip) return;
            h.LoggedShip = line;
            Plugin.Log.LogInfo(line);
        }

        /// <summary>Driven from EctypeDriver.Update: runs the pass twice a second, and on the frame
        /// after a topology change or a ship set.</summary>
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

        private static void BindTopology()
        {
            if (_topologyBound) return;
            _topologyBound = true;
            Topology.Changed += () => _passDue = true;
        }

        /// <summary>Re-resolves every holder and rebuilds only those whose ship changed: a new
        /// resolution, a pending holder whose ship now exists, or a ship destroyed under it.</summary>
        private static void Pass()
        {
            foreach (Holder h in _holders.ToArray())
            {
                if (h.Loader == null)
                {
                    RemoveFromShip(h);
                    _holders.Remove(h);
                    continue;
                }
                if (h.Applied.Count == 0) continue;
                string source;
                PlayerControlledShip now = Resolve(h, out source);
                bool destroyed = !ReferenceEquals(h.Ship, null) && h.Ship == null;
                if (!destroyed && ReferenceEquals(now, h.Ship)) continue;
                try { Reapply(h.Loader); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: characterShip rebuild for a ship change failed: " + e);
                }
            }
        }

        private static void RemoveFromShip(Holder h)
        {
            // A destroyed ship took its collection with it; there is nothing to remove from.
            if (h.Ship != null && h.Ship.Stats != null)
                foreach (StatMod mod in h.Applied)
                    h.Ship.Stats.RemoveModifier(mod.Mod.Source);
            foreach (StatTagCollection root in h.Detached)
            {
                if (!Alive(root)) continue;
                foreach (StatMod mod in h.Applied)
                    root.RemoveModifier(mod.Mod.Source);
            }
            h.Detached.Clear();
            h.Applied.Clear();
            h.Ship = null;
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
                if (_holders[i].Detached.Contains(child)) hit = true;
            if (!hit) return;
            foreach (Holder h in _holders.ToArray())
                if (h.Loader != null) Reapply(h.Loader);
        }

        private static void LogState(Holder h)
        {
            string state = h.Applied.Count + (h.Applied.Count > 0 && h.Ship == null ? " pending" : "") +
                           "/" + h.Detached.Count;
            if (state == h.LoggedState) return;
            h.LoggedState = state;
            string who = "actor ?";
            try { if (h.Player != null) who = "actor " + h.Player.ActorId; }
            catch (Exception) { }
            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: characterShip - " + who + " contributes " +
                                                   h.Applied.Count + " modifier(s)" +
                                                   (h.Applied.Count > 0 && h.Ship == null
                                                    ? ", waiting for a player ship." : " to the player ship") +
                                                   (h.Detached.Count > 0
                                                    ? " and " + h.Detached.Count + " detached module tree(s)." : "."));
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
                        _boundGame.OnPlayerShipSet -= OnShipSet;
                        if (_busBound && _boundGame.ModelEventBus != null)
                            _boundGame.ModelEventBus.OnPlayerRemoved.Unsubscribe(OnPlayerRemoved);
                    }
                    catch (Exception) { }
                }
                game.OnPlayerShipSet += OnShipSet;
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

        /// <summary>ClientGame.OnPlayerShipSet: a ship finished spawning. Each ship's start raises
        /// it, so a mission with several ships raises it several times; the pass these requests
        /// trigger runs once, on the next frame, and moves only holders whose ship changed.</summary>
        private static void OnShipSet()
        {
            _passDue = true;
        }

        /// <summary>ModelEventBus.OnPlayerRemoved: the leaving player's contribution comes off
        /// the ship. Published after the character's destroy is queued, so match by
        /// reference rather than through the character.</summary>
        private static void OnPlayerRemoved(CG.Game.Player.Player player)
        {
            try
            {
                for (int i = _holders.Count - 1; i >= 0; i--)
                {
                    Holder h = _holders[i];
                    if (!ReferenceEquals(h.Player, player) && h.Loader != null) continue;
                    RemoveFromShip(h);
                    LogState(h);
                    _holders.RemoveAt(i);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: characterShip player-removal handling failed: " + e);
            }
        }
    }

    /// <summary>
    /// Postfix: applies the holder's characterShip effects after vanilla has applied the other
    /// three targets. Finalizer: a throw inside ReApplyPerks skips the postfix but has already
    /// run ResetPerks, which took the contribution off - the finalizer puts it back. The
    /// postfix also reports framework effects that reach no stat in their target's tree.
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
            { Plugin.Log.LogError("Ectype: characterShip reapply failed: " + e); }
        }
    }

    /// <summary>Postfix: a detached module root that gains a parent has just had the parent's
    /// modifiers replayed into it; the characterShip contribution is rebuilt so it sits there
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
            { Plugin.Log.LogError("Ectype: characterShip re-parent handling failed: " + e); }
        }
    }

    /// <summary>Postfix: ResetPerks removes the player-sourced modifiers and the parked seat
    /// lists; this removes the holder's characterShip modifiers alongside them.</summary>
    [HarmonyPatch(typeof(PlayerPerkLoader), "ResetPerks")]
    internal static class Patch_PlayerPerkLoader_ResetPerks_CharacterShip
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerPerkLoader __instance)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            try { CharacterShipRoute.Remove(__instance); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: characterShip reset failed: " + e); }
        }
    }
}
