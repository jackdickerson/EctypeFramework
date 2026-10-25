using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Objects;
using CG.Ship.Object;
using Gameplay.CompositeWeapons;
using Gameplay.Loot;
using Gameplay.Tags;
using HarmonyLib;
using ResourceAssets;
using VC.Common.CoreData;

namespace Ectype
{
    /// <summary>One runtime-registered item added to the game's loot rolls.</summary>
    public sealed class LootDrop
    {
        /// <summary>The registering plugin's BepInEx GUID. Names the registration in log lines
        /// and decides which registration of an item stands.</summary>
        public string Owner;

        /// <summary>The item. Anything the loot spawners can spawn by GUID: a composite weapon
        /// spawns as its build box, every other GUID through the object factory as itself.</summary>
        public GUIDUnion Item;

        /// <summary>The rarity bucket it rolls in. <see cref="LootTables.ToLootRarity"/> maps
        /// the tooltip rarity onto it by name.</summary>
        public LootRarities Rarity = LootRarities.Common;

        /// <summary>
        /// Which stock entries place this item. A roll gets the item only when its table holds
        /// at least one authored entry this accepts; the item then copies the smallest amount
        /// and the union of spawn locations across those entries. Entries added at runtime are
        /// never anchors. Required.
        /// </summary>
        public Func<GUIDUnion, bool> IsAnchor;

        /// <summary>Optional, evaluated at roll time; false leaves the item out of that roll.
        /// Null means always.</summary>
        public Func<bool> Gate;

        /// <summary>
        /// Optional. True: join a roll only when the table's authored entries already include
        /// one at <see cref="Rarity"/>. A roll asks its table for a rarity and takes the next
        /// entry of it, so an item that is alone in its rarity drops every time that rarity is
        /// asked for, and a rarity the table never offered starts producing drops. With this
        /// set the item only ever joins a rarity the table already offers. False (the default)
        /// joins at its rarity whatever the table holds.
        /// </summary>
        public bool RequireRarityInTable;

        /// <summary>Optional. Asset names of tables the item never joins, whatever its anchor
        /// finds there; matched against the table's name without the per-roll copy's
        /// "(Clone)" suffix, e.g. "LootTable_OneShot_Collector".</summary>
        public string[] ExcludeTables;
    }

    /// <summary>
    /// Puts runtime-registered items into the game's loot drops.
    ///
    /// Every roll path copies a LootTable asset with Object.Instantiate, calls ApplyLimiters
    /// on the copy, then shuffles it. A postfix on ApplyLimiters therefore reaches every roll
    /// without knowing which asset each path reads, and lands before the shuffle, so an added
    /// entry takes part in the seeded order like a stock one. ApplyLimiters also replaces the
    /// copy's Loot list with a new one, so the append never reaches a list a shipped asset
    /// holds; the postfix additionally refuses any table whose name lacks Unity's "(Clone)"
    /// suffix, which turns "every caller instantiates first" from an observation into a check.
    ///
    /// Placement comes from the table itself. An item is added only to a table that already
    /// holds one of its anchor items, at the smallest stock amount and the union of the stock
    /// spawn locations among those anchors, at its own rarity; an item that opts in is also
    /// kept out of a table that offers no entry at its rarity. Rolls that spawn anything run
    /// on the master client; the sector on-death table is also built on every other client,
    /// where it only feeds the remaining-loot count.
    ///
    /// Anchors that resolve a prefab are warmed in the background once the player ship
    /// exists; see the warm-up section below.
    /// </summary>
    public static class LootTables
    {
        private sealed class Record
        {
            public LootDrop Drop;
            public bool? LastGate;
            public bool GateThrewLogged;
        }

        private static readonly List<Record> _records = new List<Record>();
        private static readonly HashSet<string> _loggedAdds = new HashSet<string>();
        private static readonly HashSet<string> _loggedNotCopies = new HashSet<string>();
        private static readonly HashSet<string> _loggedRarityMisses = new HashSet<string>();
        private static readonly HashSet<string> _loggedTagMisses = new HashSet<string>();
        private static readonly HashSet<string> _loggedContentHolds = new HashSet<string>();
        private static bool _patched;
        private static bool _postfixFailedLogged;
        private static string _patchStatus = "not patched yet";

        /// <summary>Patch state and registration count, for a menu or a log line.</summary>
        public static string Status
        {
            get { return _patchStatus + ", " + _records.Count + " item(s) registered"; }
        }

        /// <summary>
        /// Register an item. Safe to call again: a repeat for an item the same owner already
        /// registered is a no-op that returns true, so a registration driver can retry freely.
        /// Returns false, with a log line, for a missing item or anchor, or for an item another
        /// owner registered first.
        /// </summary>
        public static bool Register(LootDrop drop)
        {
            try
            {
                if (drop == null || drop.Item.IsNull || drop.IsAnchor == null)
                {
                    Plugin.Log.LogError("Ectype: loot: a registration needs an item GUID and an " +
                                        "anchor; ignored (" + (drop == null ? "null drop" :
                                        Describe(drop)) + ").");
                    return false;
                }
                Identifiers.WarnOwner("loot owner", drop.Owner);
                string owner = string.IsNullOrEmpty(drop.Owner) ? "unnamed" : drop.Owner;
                // Publications still pending count too: a mod may publish and register loot in
                // the same Awake, before the containers exist.
                if (RuntimeContent.IsComponent(drop.Item))
                {
                    Plugin.Log.LogError("Ectype: loot: " + Describe(drop) + " is published as a " +
                                        "component, which never drops on its own; ignored.");
                    return false;
                }
                foreach (Record r in _records)
                {
                    if (r.Drop.Item != drop.Item) continue;
                    if (r.Drop.Owner == owner) return true;
                    Plugin.Log.LogWarning("Ectype: loot: " + owner + " tried to register " +
                                          drop.Item.AsHex() + ", which " + r.Drop.Owner +
                                          " already registered; the first registration stands.");
                    return false;
                }

                // A copy, so a caller changing its object afterwards cannot change a live roll.
                LootDrop copy = new LootDrop
                {
                    Owner = owner,
                    Item = drop.Item,
                    Rarity = drop.Rarity,
                    IsAnchor = drop.IsAnchor,
                    Gate = drop.Gate,
                    RequireRarityInTable = drop.RequireRarityInTable,
                    ExcludeTables = drop.ExcludeTables == null
                        ? null : (string[])drop.ExcludeTables.Clone(),
                };
                _records.Add(new Record { Drop = copy });
                Plugin.Log.LogInfo("Ectype: loot: " + Describe(copy) + " registered" +
                                   (copy.RequireRarityInTable
                                    ? ", only into tables that already offer " + copy.Rarity : "") +
                                   (_patched ? "." : "; the loot patch is not applied (" +
                                    _patchStatus + "), so it will not drop."));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: loot: registration threw: " + e);
                return false;
            }
        }

        /// <summary>True once the item is registered, by anyone.</summary>
        public static bool IsRegistered(GUIDUnion item)
        {
            foreach (Record r in _records)
                if (r.Drop.Item == item) return true;
            return false;
        }

        // ------------------------------------------------------------------ anchors

        /// <summary>Anchor on any of these items.</summary>
        public static Func<GUIDUnion, bool> AnyOf(IEnumerable<GUIDUnion> items)
        {
            HashSet<GUIDUnion> set = new HashSet<GUIDUnion>(items ?? new GUIDUnion[0]);
            return g => set.Contains(g);
        }

        private static Func<GUIDUnion, bool> _anyCompositeWeapon;

        /// <summary>Anchor on any composite weapon: the tables stock weapons drop from.</summary>
        public static Func<GUIDUnion, bool> AnyCompositeWeapon
        {
            get
            {
                if (_anyCompositeWeapon == null)
                {
                    _anyCompositeWeapon = g =>
                    {
                        CompositeWeaponDataContainer c = CompositeWeaponDataContainer.Instance;
                        CompositeWeaponDataDef def;
                        return c != null && c.TryGetByGuid(g, out def);
                    };
                }
                return _anyCompositeWeapon;
            }
        }

        /// <summary>
        /// Anchor on any carryable whose asset is a <typeparamref name="T"/>, for example every
        /// payload battery. Each GUID is resolved once and the answer kept, so a roll does not
        /// load a prefab per entry.
        /// </summary>
        public static Func<GUIDUnion, bool> AnyCarryable<T>() where T : class
        {
            Dictionary<GUIDUnion, bool> seen = new Dictionary<GUIDUnion, bool>();
            return g =>
            {
                bool hit;
                if (seen.TryGetValue(g, out hit)) return hit;
                CarryableContainer c = CarryableContainer.Instance;
                if (c == null) return false;   // not cached: the container may come up later
                CarryableDef def;
                hit = c.TryGetByGuid(g, out def) && def != null && def.Asset is T;
                seen[g] = hit;
                return hit;
            };
        }

        /// <summary>
        /// Anchor on any carryable whose prefab carries one of these stock tags, for example
        /// "Carryable_WeaponMod" for every weapon mod and no relic. Names resolve against the
        /// game's tag table the way a manifest tag does ("Tag_" optional, case ignored) and
        /// match by reference, as the game's own tag filters do; a name that does not resolve
        /// is logged once and ignored. Each GUID is resolved once and the answer kept.
        /// </summary>
        public static Func<GUIDUnion, bool> AnyCarryableTagged(params string[] tagNames)
        {
            string[] names = tagNames == null ? new string[0] : (string[])tagNames.Clone();
            Dictionary<GUIDUnion, bool> seen = new Dictionary<GUIDUnion, bool>();
            CsTag[] wanted = null;
            return g =>
            {
                bool hit;
                if (seen.TryGetValue(g, out hit)) return hit;
                CarryableContainer c = CarryableContainer.Instance;
                if (c == null) return false;   // not cached: the container may come up later
                if (wanted == null)
                {
                    wanted = ResolveAnchorTags(names);
                    if (wanted == null) return false;   // tag table not up yet: not cached
                }
                hit = false;
                CarryableDef def;
                if (c.TryGetByGuid(g, out def) && def != null)
                {
                    CarryableObject obj = def.Asset;
                    if (obj != null) hit = CarriesAny(obj.CsTags, wanted);
                }
                seen[g] = hit;
                return hit;
            };
        }

        /// <summary>The live tags for an anchor's names, or null while the tag table is not
        /// loaded. Names that do not resolve are dropped, so the result may be empty.</summary>
        private static CsTag[] ResolveAnchorTags(string[] names)
        {
            if (DataTable<CsTagTable>.Instance == null) return null;
            List<CsTag> tags = new List<CsTag>(names.Length);
            foreach (string n in names)
            {
                try { tags.Add(TagRegistry.Resolve(n)); }
                catch (Exception e)
                {
                    if (_loggedTagMisses.Add(n ?? ""))
                        Plugin.Log.LogWarning("Ectype: loot: anchor tag '" + n + "' did not " +
                                              "resolve (" + e.Message + "); it matches nothing.");
                }
            }
            return tags.ToArray();
        }

        private static bool CarriesAny(CsTag[] carried, CsTag[] wanted)
        {
            if (carried == null) return false;
            foreach (CsTag t in carried)
            {
                if (t == null) continue;
                foreach (CsTag w in wanted)
                    if (ReferenceEquals(t, w)) return true;
            }
            return false;
        }

        /// <summary>The loot rarity for a tooltip rarity. The two enums do not share values,
        /// so they are mapped by name.</summary>
        public static LootRarities ToLootRarity(RarityType rarity)
        {
            switch (rarity)
            {
                case RarityType.Rare: return LootRarities.Rare;
                case RarityType.Epic: return LootRarities.Epic;
                case RarityType.Legendary: return LootRarities.Legendary;
                case RarityType.Special: return LootRarities.Special;
                default: return LootRarities.Common;
            }
        }

        // ------------------------------------------------------------------ the patch

        /// <summary>
        /// Patches LootTable.ApplyLimiters, resolved by name. Called from the framework's
        /// Awake inside its own try/catch, after the attribute patch pass, so a renamed member
        /// costs loot alone and never the rest of the framework.
        /// </summary>
        internal static void ApplyPatch(Harmony harmony)
        {
            MethodInfo target = typeof(LootTable).GetMethod("ApplyLimiters",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (target == null)
            {
                _patchStatus = "inert: LootTable.ApplyLimiters not found";
                Plugin.Log.LogError("Ectype: loot: LootTable.ApplyLimiters not found (game " +
                                    "update?); registered items will not drop.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(LootTables).GetMethod(
                "ApplyLimitersPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
            _patched = true;
            _patchStatus = "patched";
            Plugin.Log.LogInfo("Ectype: loot: LootTable.ApplyLimiters patched.");
        }

        private struct Baseline
        {
            public bool Found;
            public int Amount;
            public LootSpawnLocationType Where;
        }

        private sealed class Added
        {
            public int Count;
            public int Amount;
            public LootSpawnLocationType Where;
        }

        private static void ApplyLimitersPostfix(LootTable __instance)
        {
            try
            {
                if (_records.Count == 0 || __instance == null || __instance.Loot == null) return;

                string name = __instance.name ?? "";
                if (!name.EndsWith("(Clone)", StringComparison.Ordinal))
                {
                    if (_loggedNotCopies.Add(name))
                        Plugin.Log.LogWarning("Ectype: loot: ApplyLimiters ran on '" + name +
                                              "', which is not a per-roll copy; nothing added, " +
                                              "so no shipped table is ever written.");
                    return;
                }
                string table = name.Substring(0, name.Length - "(Clone)".Length).TrimEnd();

                HashSet<GUIDUnion> present = new HashSet<GUIDUnion>();
                foreach (LootTableEntry e in __instance.Loot)
                    if (e != null && e.ItemRef != null) present.Add(e.ItemRef.AssetGuid);

                Dictionary<Func<GUIDUnion, bool>, Baseline> baselines = null;
                Dictionary<Func<bool>, bool> gates = null;
                Dictionary<string, Added> addedBy = null;
                LootRarities? offered = null;

                // A snapshot: a gate is foreign code and could register mid-loop.
                foreach (Record r in _records.ToArray())
                {
                    LootDrop d = r.Drop;
                    if (present.Contains(d.Item)) continue;
                    if (d.ExcludeTables != null && Array.IndexOf(d.ExcludeTables, table) >= 0) continue;
                    if (HeldByContent(d)) continue;

                    if (baselines == null) baselines = new Dictionary<Func<GUIDUnion, bool>, Baseline>();
                    Baseline b;
                    if (!baselines.TryGetValue(d.IsAnchor, out b))
                    {
                        b = Measure(__instance.Loot, d.IsAnchor);
                        baselines[d.IsAnchor] = b;
                    }
                    if (!b.Found) continue;

                    if (d.RequireRarityInTable)
                    {
                        if (offered == null) offered = AuthoredRarities(__instance.Loot);
                        if ((offered.Value & d.Rarity) == 0)
                        {
                            if (_loggedRarityMisses.Add(d.Owner + "|" + table + "|" + d.Rarity))
                                Plugin.Log.LogInfo("Ectype: loot: " + Describe(d) + " left out of " +
                                                   table + ", which offers no " + d.Rarity +
                                                   " entry. Logged once per table.");
                            continue;
                        }
                    }

                    if (d.Gate != null)
                    {
                        if (gates == null) gates = new Dictionary<Func<bool>, bool>();
                        bool open;
                        if (!gates.TryGetValue(d.Gate, out open))
                        {
                            open = EvaluateGate(r);
                            gates[d.Gate] = open;
                        }
                        if (r.LastGate != open)
                        {
                            r.LastGate = open;
                            Plugin.Log.LogInfo("Ectype: loot: gate for " + Describe(d) + " is now " +
                                               (open ? "open" : "closed") + " (roll on " + table + ").");
                        }
                        if (!open) continue;
                    }

                    __instance.Loot.Add(new LootTableEntry
                    {
                        ItemRef = new CraftableItemRef { AssetGuid = d.Item, IsRuntime = true },
                        Amount = b.Amount,
                        LootRarity = d.Rarity,
                        PossibleSpawnLocationTypes = b.Where,
                    });
                    present.Add(d.Item);

                    if (addedBy == null) addedBy = new Dictionary<string, Added>();
                    Added a;
                    if (!addedBy.TryGetValue(d.Owner, out a)) addedBy[d.Owner] = a = new Added();
                    a.Count++;
                    a.Amount = b.Amount;
                    a.Where |= b.Where;
                }

                if (addedBy == null) return;
                foreach (KeyValuePair<string, Added> kv in addedBy)
                {
                    if (!_loggedAdds.Add(kv.Key + "|" + table)) continue;
                    Plugin.Log.LogInfo("Ectype: loot: " + kv.Key + " added " + kv.Value.Count +
                                       " item(s) to " + table + " (amount " + kv.Value.Amount +
                                       ", locations " + kv.Value.Where + "). Logged once per table.");
                }
            }
            catch (Exception e)
            {
                if (_postfixFailedLogged) return;
                _postfixFailedLogged = true;
                Plugin.Log.LogError("Ectype: loot: ApplyLimiters postfix failed (reported once): " + e);
            }
        }

        /// <summary>The smallest amount and the union of spawn locations across the table's
        /// authored entries the anchor accepts. Runtime entries are skipped, so one mod's added
        /// items never become another's baseline.</summary>
        private static Baseline Measure(List<LootTableEntry> loot, Func<GUIDUnion, bool> isAnchor)
        {
            Baseline b = new Baseline { Amount = int.MaxValue };
            foreach (LootTableEntry e in loot)
            {
                if (e == null || e.ItemRef == null || e.ItemRef.IsRuntime) continue;
                bool anchor;
                try { anchor = isAnchor(e.ItemRef.AssetGuid); }
                catch { anchor = false; }
                if (!anchor) continue;
                b.Found = true;
                b.Amount = Math.Min(b.Amount, Math.Max(1, e.Amount));
                b.Where |= e.PossibleSpawnLocationTypes;
            }
            if (!b.Found) b.Amount = 0;
            return b;
        }

        /// <summary>Every rarity the table's authored entries use. Runtime entries are skipped
        /// for the same reason as in <see cref="Measure"/>.</summary>
        private static LootRarities AuthoredRarities(List<LootTableEntry> loot)
        {
            LootRarities r = 0;
            foreach (LootTableEntry e in loot)
                if (e != null && e.ItemRef != null && !e.ItemRef.IsRuntime) r |= e.LootRarity;
            return r;
        }

        /// <summary>
        /// The item's published content entry, when it has one, also decides: a component never
        /// joins a roll, and an entry that is not available in this room keeps the item out of
        /// the roll as a closed gate does. Each answer is logged once per item.
        /// </summary>
        private static bool HeldByContent(LootDrop d)
        {
            ModContentEntry e = ModContent.Find(d.Item);
            string why = RuntimeContent.IsComponent(d.Item) ? "it is published as a component"
                       : e != null && !ModContent.Available(e) ? "its content entry is not available in this room"
                       : null;
            if (why == null) return false;
            if (_loggedContentHolds.Add(d.Item.AsHex() + "|" + why))
                Plugin.Log.LogInfo("Ectype: loot: " + Describe(d) + " left out of rolls: " + why +
                                   ". Logged once.");
            return true;
        }

        /// <summary>A gate that throws counts as closed: an item must never drop on a check
        /// that did not run.</summary>
        private static bool EvaluateGate(Record r)
        {
            try { return r.Drop.Gate(); }
            catch (Exception e)
            {
                if (!r.GateThrewLogged)
                {
                    r.GateThrewLogged = true;
                    Plugin.Log.LogError("Ectype: loot: gate for " + Describe(r.Drop) +
                                        " threw; treated as closed (reported once): " + e);
                }
                return false;
            }
        }

        private static string Describe(LootDrop d)
        {
            return (string.IsNullOrEmpty(d.Owner) ? "unnamed" : d.Owner) + " " +
                   d.Item.AsHex() + " (" + d.Rarity + ")";
        }

        // ------------------------------------------------------------------ warm-up
        //
        // An anchor that resolves a carryable's prefab does it through the game's own
        // resource ref, which is a synchronous Resources.Load: the first roll to meet an
        // unloaded item stalls on it. The warm-up runs every registered anchor over every item
        // in every table before the first roll, a little per frame, so the anchors' caches
        // are full when rolls start. It changes no answer: a roll that comes first takes the
        // lazy path, exactly as without it.
        //
        // Every client warms. The sector on-death table is built, and so passes through the
        // postfix, on every client at every sector entry; only the spawning rolls are
        // master-only, and the master can change mid-run.

        /// <summary>Every LootTable the game ships sits in this Resources folder and nothing
        /// else does, so loading the folder enumerates them without loading the ships, wrecks
        /// and quests that reference them.</summary>
        private const string LootTableFolder = "Loot";

        /// <summary>Stopwatch-measured warm-up work per frame. Asynchronous load integration
        /// runs outside it, time-sliced by Unity.</summary>
        private const double WarmBudgetMs = 1.0;

        /// <summary>Prefab loads in flight at once. Kept low because a synchronous load the
        /// game makes meanwhile shares the one loading thread with them.</summary>
        private const int WarmMaxLoads = 2;

        /// <summary>Delay after the player ship appears, so the warm-up's one unsplittable
        /// step (collecting the tables) does not land on the ship's spawn frame.</summary>
        private const float WarmSettleSeconds = 1f;

        private sealed class WarmPass
        {
            public readonly List<Func<GUIDUnion, bool>> Anchors = new List<Func<GUIDUnion, bool>>();
            public readonly List<GUIDUnion> Items = new List<GUIDUnion>();
            public readonly List<KeyValuePair<GUIDUnion, UnityEngine.ResourceRequest>> Loading =
                new List<KeyValuePair<GUIDUnion, UnityEngine.ResourceRequest>>();
            public readonly System.Diagnostics.Stopwatch Wall = System.Diagnostics.Stopwatch.StartNew();
            public int Next;
            public int Tables, FromFolder, FromLoaded;
            public int Preloaded, Calls, Throws, Frames;
            public double WorstFrameMs, WorstCallMs, LongestFrameMs;
        }

        private static readonly HashSet<Func<GUIDUnion, bool>> _warmed = new HashSet<Func<GUIDUnion, bool>>();
        private static readonly System.Diagnostics.Stopwatch _frameWatch = new System.Diagnostics.Stopwatch();
        private static WarmPass _pass;
        private static CG.Game.ClientGame _warmGame;
        private static bool _shipSeen;
        private static float _warmAt;
        private static bool _warmBroken;

        /// <summary>
        /// The warm-up's per-frame step, called from the framework driver. Waits for the
        /// player ship (ClientGame.OnPlayerShipSet), then warms any anchor not yet warmed,
        /// within <see cref="WarmBudgetMs"/> a frame. Anchors registered later are warmed by a
        /// further pass.
        /// </summary>
        internal static void PollWarmUp()
        {
            if (_warmBroken || !_patched) return;
            try
            {
                BindShipSignal();
                if (_pass != null) { StepWarmUp(); return; }
                if (!_shipSeen || UnityEngine.Time.unscaledTime < _warmAt) return;
                BeginWarmUp();
            }
            catch (Exception e)
            {
                _warmBroken = true;
                _pass = null;
                Plugin.Log.LogError("Ectype: loot: anchor warm-up failed and is off for this " +
                                    "session; rolls resolve their items as they meet them: " + e);
            }
        }

        /// <summary>OnPlayerShipSet is an instance member, so each new ClientGame needs its
        /// own subscription. A game whose ship already exists has already raised it.</summary>
        private static void BindShipSignal()
        {
            CG.Game.ClientGame game = CG.Game.ClientGame.Current;
            if (ReferenceEquals(game, null) || ReferenceEquals(game, _warmGame)) return;
            if (!ReferenceEquals(_warmGame, null))
            {
                try { _warmGame.OnPlayerShipSet -= OnShipSetForWarmUp; }
                catch (Exception) { }
            }
            game.OnPlayerShipSet += OnShipSetForWarmUp;
            _warmGame = game;
            // Presence only: any ship having been set counts, whichever ship the getter names.
            if (game.PlayerShip != null) OnShipSetForWarmUp();
        }

        private static void OnShipSetForWarmUp()
        {
            try
            {
                if (_shipSeen) return;
                _shipSeen = true;
                _warmAt = UnityEngine.Time.unscaledTime + WarmSettleSeconds;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: loot: ship-set handling for the warm-up failed: " + e);
            }
        }

        /// <summary>One frame: collect the anchors still cold, the tables and their items.
        /// Returns without a pass when every anchor is warm.</summary>
        private static void BeginWarmUp()
        {
            WarmPass p = null;
            foreach (Record r in _records)
            {
                Func<GUIDUnion, bool> a = r.Drop.IsAnchor;
                if (_warmed.Contains(a)) continue;
                if (p == null) p = new WarmPass();
                if (!p.Anchors.Contains(a)) p.Anchors.Add(a);
            }
            if (p == null) return;

            _frameWatch.Reset();
            _frameWatch.Start();
            HashSet<LootTable> tables = new HashSet<LootTable>();
            try
            {
                foreach (LootTable t in UnityEngine.Resources.LoadAll<LootTable>(LootTableFolder))
                    if (t != null && tables.Add(t)) p.FromFolder++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: loot: loading Resources/" + LootTableFolder +
                                      " for the warm-up failed: " + e.Message);
            }
            // Tables loaded from anywhere else; per-roll copies are left out.
            foreach (LootTable t in UnityEngine.Resources.FindObjectsOfTypeAll<LootTable>())
            {
                if (t == null) continue;
                string n = t.name ?? "";
                if (n.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                if (tables.Add(t)) p.FromLoaded++;
            }
            p.Tables = tables.Count;
            if (p.FromFolder == 0)
                Plugin.Log.LogWarning("Ectype: loot: Resources/" + LootTableFolder + " held no " +
                                      "loot tables (game update?); warming only the " + p.Tables +
                                      " already loaded.");

            HashSet<GUIDUnion> seen = new HashSet<GUIDUnion>();
            foreach (LootTable t in tables)
            {
                if (t.Loot == null) continue;
                foreach (LootTableEntry e in t.Loot)
                {
                    if (e == null || e.ItemRef == null || e.ItemRef.IsRuntime) continue;
                    if (e.ItemRef.AssetGuid.IsNull) continue;
                    if (seen.Add(e.ItemRef.AssetGuid)) p.Items.Add(e.ItemRef.AssetGuid);
                }
            }
            _frameWatch.Stop();
            p.Frames = 1;
            p.WorstFrameMs = _frameWatch.Elapsed.TotalMilliseconds;
            _pass = p;
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: loot: warm-up started - " + p.Anchors.Count +
                                   " anchor(s) over " + p.Items.Count + " item(s) in " +
                                   p.Tables + " table(s).");
        }

        /// <summary>One budgeted frame: evaluate the items whose prefab load finished, then
        /// start loads for the next items, evaluating at once any item with nothing to load
        /// (a composite weapon, or a GUID no container holds).</summary>
        private static void StepWarmUp()
        {
            WarmPass p = _pass;
            p.Frames++;
            p.LongestFrameMs = Math.Max(p.LongestFrameMs, UnityEngine.Time.unscaledDeltaTime * 1000.0);
            _frameWatch.Reset();
            _frameWatch.Start();

            for (int i = 0; i < p.Loading.Count && _frameWatch.Elapsed.TotalMilliseconds < WarmBudgetMs; )
            {
                UnityEngine.ResourceRequest req = p.Loading[i].Value;
                if (req != null && !req.isDone) { i++; continue; }
                GUIDUnion g = p.Loading[i].Key;
                p.Loading.RemoveAt(i);
                EvaluateAnchors(p, g);
            }

            while (p.Next < p.Items.Count && _frameWatch.Elapsed.TotalMilliseconds < WarmBudgetMs)
            {
                GUIDUnion g = p.Items[p.Next];
                string path = PrefabPath(g);
                if (path != null)
                {
                    if (p.Loading.Count >= WarmMaxLoads) break;
                    p.Next++;
                    // The same call the game's own ResourceAssetRef.PreloadAssetAsync makes.
                    UnityEngine.ResourceRequest req = UnityEngine.Resources.LoadAsync<UnityEngine.Object>(path);
                    if (req != null)
                    {
                        p.Preloaded++;
                        p.Loading.Add(new KeyValuePair<GUIDUnion, UnityEngine.ResourceRequest>(g, req));
                        continue;
                    }
                }
                else
                {
                    p.Next++;
                }
                EvaluateAnchors(p, g);
            }

            _frameWatch.Stop();
            p.WorstFrameMs = Math.Max(p.WorstFrameMs, _frameWatch.Elapsed.TotalMilliseconds);
            if (p.Next >= p.Items.Count && p.Loading.Count == 0) FinishWarmUp(p);
        }

        /// <summary>The Resources path a stock carryable's prefab loads from, or null when the
        /// GUID is not a stock carryable.</summary>
        private static string PrefabPath(GUIDUnion g)
        {
            CarryableContainer c = CarryableContainer.Instance;
            CarryableDef def;
            if (c == null || !c.TryGetByGuid(g, out def) || def == null || def.Ref == null) return null;
            if (def.Ref.IsRuntime) return null;
            ResourcePaths paths = ResourcePaths.Instance;
            string path;
            if (paths == null || !paths.TryGetPath(g, out path) || string.IsNullOrEmpty(path)) return null;
            return path;
        }

        /// <summary>Runs every cold anchor on one item. A throw counts as "not an anchor", as
        /// it does in a roll.</summary>
        private static void EvaluateAnchors(WarmPass p, GUIDUnion g)
        {
            foreach (Func<GUIDUnion, bool> a in p.Anchors)
            {
                long t0 = _frameWatch.ElapsedTicks;
                try { a(g); }
                catch (Exception) { p.Throws++; }
                p.Calls++;
                double ms = (_frameWatch.ElapsedTicks - t0) * 1000.0 /
                            System.Diagnostics.Stopwatch.Frequency;
                if (ms > p.WorstCallMs) p.WorstCallMs = ms;
            }
        }

        private static void FinishWarmUp(WarmPass p)
        {
            foreach (Func<GUIDUnion, bool> a in p.Anchors) _warmed.Add(a);
            _pass = null;
            p.Wall.Stop();
            Plugin.Log.LogInfo(string.Format(
                "Ectype: loot: anchor warm-up done - {0} table(s) ({1} from Resources/{2}, {3} " +
                "loaded elsewhere), {4} item(s), {5} anchor(s), {6} prefab(s) loaded " +
                "asynchronously; {7:0} ms over {8} frame(s). Worst frame of warm-up work " +
                "{9:0.00} ms (budget {10:0.0} ms), slowest anchor call {11:0.00} ms{12}; longest " +
                "frame while warming {13:0.0} ms, everything the game did included " +
                "(background loading priority {14}).",
                p.Tables, p.FromFolder, LootTableFolder, p.FromLoaded, p.Items.Count,
                p.Anchors.Count, p.Preloaded, p.Wall.Elapsed.TotalMilliseconds, p.Frames,
                p.WorstFrameMs, WarmBudgetMs, p.WorstCallMs,
                p.Throws > 0 ? ", " + p.Throws + " call(s) threw" : "",
                p.LongestFrameMs, UnityEngine.Application.backgroundLoadingPriority));
        }
    }
}
