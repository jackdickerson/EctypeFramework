# Loot tables: dropping a runtime item where stock items drop

`LootTables` adds an item your mod registered at runtime to the game's stock loot rolls.
Register each item once its asset is published. The call is idempotent, so a registration
driver may retry it.

```csharp
LootTables.Register(new LootDrop
{
    Owner    = "My mod",                                   // log lines only
    Item     = myItemGuid,
    Rarity   = LootTables.ToLootRarity(RarityType.Rare),   // the two enums differ; mapped by name
    IsAnchor = LootTables.AnyCarryable<PayloadBattery>(),  // which stock entries place it
    Gate     = () => ClassRegistry.IsAvailableInRoom(MyId), // optional, asked at each roll
    RequireRarityInTable = true,                            // optional, default false
    ExcludeTables = new[] { "LootTable_OneShot_Collector" }, // optional, by asset name
});
```

## How a roll reaches your item

Every roll path copies a `LootTable` with `Object.Instantiate`, calls `ApplyLimiters` on the
copy, then shuffles it with a seed. The roll paths are the sector on-death table, survivor
waves, wreck and platform loot areas, custom on-death droppers, multi-loot stations and
dynamic loot adders. The framework postfixes `ApplyLimiters`, so one patch reaches every roll,
and it runs before the shuffle, so an added entry takes part in the seeded order like a stock
one. `ApplyLimiters` replaces the copy's `Loot` with a fresh list. The postfix also refuses a
table whose name lacks Unity's `(Clone)` suffix, so no shipped asset is ever written. Nothing
outlives the process.

## Placement and anchors

Placement comes from the table. An item joins a roll only when the table holds an authored
entry its anchor accepts. It then takes the smallest anchor `Amount` and the union of the
anchors' spawn locations, at its own rarity. Entries added at runtime are never anchors, so
two mods' items never become each other's baseline.

| Anchor | Accepts |
| --- | --- |
| `AnyCompositeWeapon` | tables stock weapons drop from |
| `AnyCarryable<T>()` | any carryable whose asset is a `T` |
| `AnyCarryableTagged(names)` | any carryable whose prefab carries one of the named stock tags, matched by reference; `"Carryable_WeaponMod"` is every weapon mod and no relic |
| `AnyOf(guids)` | those GUIDs |

The two carryable anchors resolve each GUID's prefab once and keep the answer. A gate that
returns false or throws leaves the item out of that roll. The first change of answer is
logged.

## Rarity match

A roll asks its table for a rarity and takes the next entry of it. An item alone in its
rarity therefore drops every time that rarity is asked for, and a rarity the table never
offered starts producing drops. `RequireRarityInTable = true` keeps the item out of any table
whose authored entries have none at its `Rarity`; the first miss per table is logged. The
default, false, joins at the item's rarity whatever the table holds.

## Excluded tables

`ExcludeTables` names tables the item never joins, whatever its anchor finds there. Names are
the shipped asset's name; the per-roll copy's `(Clone)` suffix is stripped before the match.
Use it for a table the anchor rightly matches but where the item is unwanted, such as one so
small that the item would dominate its rarity.

## Warm-up

The prefab resolve behind the carryable anchors is the game's own synchronous
`Resources.Load`, so the first roll to meet an unloaded item stalls on it. One second after
`ClientGame.OnPlayerShipSet` fires (or after the framework finds the ship already set), the
framework runs every registered anchor over every item of every loot table, at about 1 ms of
Stopwatch-measured work a frame:

1. Tables: `Resources.LoadAll<LootTable>("Loot")`, which holds every table the game ships and
   nothing else, plus any other `LootTable` already loaded. Per-roll copies are skipped.
2. Each distinct item: a stock carryable's prefab is first loaded with
   `Resources.LoadAsync<Object>` on its def's path, the same call the game's own
   `ResourceAssetRef.PreloadAssetAsync` makes, at most two at a time. When it lands, each
   anchor runs and its synchronous load finds the prefab already in memory. Anything else (a
   composite weapon) is evaluated at once.
3. One `[Info]` line when done: tables (from the folder and from elsewhere), items, anchors,
   prefabs preloaded, wall time and frames, the worst frame of warm-up work against the
   budget, the slowest single anchor call, and the longest frame seen while warming with the
   game's own work and asynchronous load integration included.

Every client warms: the sector on-death table passes through the postfix on every client at
every sector entry, and the master can change mid-run. Correctness never depends on it. A
roll that arrives first resolves its items as it meets them, and an anchor registered after a
pass is warmed by the next one. A failure turns the warm-up off for the session with one
`[Error]` line; rolls are unaffected.

## Spawning and multiplayer

The spawn side needs no work. The floating spawn and the on-death dropper turn a
composite-weapon GUID into its build box and hand any other GUID to the object factory, which
spawns it as itself. Rolls that spawn anything run on the master. The sector on-death table is
also built on every other client, where it only counts remaining loot.

A registered item must resolve on every client in the room. In a room with a member who
cannot resolve it, close the gate. See [../multiplayer.md](../multiplayer.md) for how rooms
are composed and [selection-gate.md](selection-gate.md) for checking members.

`LootTables.Status` reports the patch state. `[Info]` lines name each table an owner's items
were first added to. If the postfix fails to apply, loot alone is affected.

Sample: [examples/loot-tables](../../examples/loot-tables/).
