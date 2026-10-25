# Content registry: publishing a weapon, module or carryable

`RuntimeContent.Publish` puts an item your mod built at runtime into the game: it registers
the item in every container its kind needs, lists it where item catalogs and the game's own
self-lookup read, writes its unlock definition, and records a `ModContentEntry` that any
catalog mod can read. One call per item replaces the hand-written registration sequence, and
every client ends up with the same lists.

```csharp
ContentHandle h = RuntimeContent.Publish(ContentKind.Carryable, new GUIDUnion(MyItemGuid),
    new ContentAssets
    {
        Asset        = myPrefab,                    // inactive prefab carrying a CarryableObject
        Description  = "What it does.",
        Icon         = myIcon,
        Crafting     = new CraftingRules            // mandatory for a carryable
        {
            CraftingMethod = CraftMethod.Resource,
            Creation       = new ResourceCurrency(ResourceType.Alloys, 30),
            CanBeRecycled  = true,
            Recycle        = new ResourceCurrency(ResourceType.Alloys, 10),
        },
        ItemCategory = PurchasableItemSubCategory.Mods_Offensive,
        Rarity       = RarityType.Rare,
    },
    new ModContentEntry
    {
        DisplayName     = "My Item",
        Owner           = MyPluginGuid,             // your BepInEx plugin GUID
        Tier            = 2,                        // 0 = unranked
        AvailableInRoom = () => RoleRegistry.IsAvailableInRoom(MyRoleId),   // optional
    });
// h.State is Registered, Pending (containers not loaded yet) or Failed with h.Reason.
```

## Kinds and what each registers

| Kind | `Asset` | Containers registered | Listed in the authored list of | Crafting data |
| --- | --- | --- | --- | --- |
| `WeaponModule` | a `CompositeWeaponData` | runtime asset register, composite-weapon, crafting, unlock | the composite-weapon container | mandatory |
| `Module` | the module prefab (GameObject or its `CellModule`) | runtime asset register, module, spawnable-object, unlock; crafting when given | the module container | optional |
| `Carryable` | the item prefab (GameObject or its `CarryableObject`) | runtime asset register, carryable, spawnable-object, crafting, unlock | the carryable container | mandatory |

Details per kind:

- **Weapon module.** Publish builds the weapon def, sets its context info and build-box icon
  by field (the context-info property setter of a purchasable def resolves the presentable
  owner of its own GUID, which is the def itself once published, and recurses until the
  stack overflows), and sets the purchase category from `ItemCategory` when given. Copy the
  category from the donor weapon's def. The unlock and crafting defs are keyed on the
  weapon's GUID: the fabricator, recycling and blank-box inscription all look the weapon up
  by that GUID, never by the shared build-box GUID.
- **Module.** Publish registers a spawnable-object def carrying the context info, and a
  module def with `ModuleCategory`; the module def reads its context info through the
  spawnable-object def. A module is installed from its build box, and the game reads a
  module's recycle value from the build box's crafting data, so publish the build box as a
  `Carryable` with its own crafting data. `Crafting` on the module itself is optional.
- **Carryable.** Publish calls the game's own `RuntimeAssetConverter.RegisterCarryableToContainers`,
  the path the game uses for carryables it converts at runtime: one spawnable-object,
  carryable, unlock and crafting def, sharing one context info. It then writes the crafting
  rules, the purchase category (`Other_Basic` when `ItemCategory` is null, as the converter
  does) and the unlock options.

Every kind:

- writes the item's GUID into the asset's container GUID, which a cloned asset otherwise
  inherits from its donor;
- registers the asset with the runtime asset register under `ModFlag`, with `DisplayName` as
  its display name. The default is `IsNetworkSpawned`, or `None` in a framework build that
  does not flag the session. That register is what a spawn by GUID resolves through, and what
  the game's join check compares between host and joiner;
- writes an unlock def with criteria `Unlock` (`Session` by default) and rarity `Rarity`.
  Every item a vanilla screen can reach needs one: the loadout terminal's item constructor
  reads the unlock def's options without a null check, and the rank panel walks every
  unlockable container and indexes the unlock container by each GUID. `Session` writes
  nothing to the player's save; the item is learned at the fabricator by recycling one.
  The game's unlock check reads `Session` as locked, so a screen that draws locked items
  greyed draws it greyed. `Always` writes the GUID into every player's persistent unlocks,
  where it stays after the mod is removed;
- resolves everything it registered back out of each container by GUID before reporting
  `Registered`, so a write that did not stick fails the attempt by name and is retried.

Publish owns every container entry for its GUID. If any of the kind's containers already
holds the GUID before the first attempt, or the kind's authored list already lists a def
under it, something else put it there, and the publication is refused rather than adopting
data it did not write. A runtime asset already in the runtime asset register is accepted as
is, and `ModFlag` is then ignored: the asset keeps the effect it was registered with.

Publish does not do the parts that are not registration: building the weapon's element and
stat graph and registering its stat descriptors, the deserialization patch that restores the
runtime flag on a weapon ref received over the network, the upgrade-chain entry that links
tiers, and loot placement (`LootTables`). Those stay with the mod.

## The ordering rule

The game keeps two lists per container: the authored list, filled from the shipped data, and
the runtime list, filled by runtime registration. Item catalogs, the debug menu's weapon list
and the game's asset-to-def lookup (`GetAssetDefBySelfRef`, which a composite weapon calls to
spawn its build box) read only the authored list, so a runtime item that is not in it is
invisible to them. The debug menu's carryable list reads the runtime list. `GetAssetIndex` and `GetFullAssetRefList` also read only the authored list,
so an item's position there is its index.

Publish therefore lists the item in its kind's authored list, and it inserts rather than
appends. Authored entries keep their places. Runtime entries form the tail of the list, and a
new one goes before the first runtime entry whose GUID sorts after its own, comparing the
32-character hex forms ordinally. A tail built this way is sorted by GUID whatever order the
publications ran in, so two clients that load mods in a different order, or that finish a
retry at a different moment, hold identical lists. Appending would make each client's list
follow its own registration order.

The game sends an index into an authored list over the network in three places: the
mutators of a session and a player's shared mutator unlocks, the ping a player places (an
index into the ping definitions), and a player's emote and multi-slot cosmetic choices. None
of those is a container Publish writes. The rule makes an index into the weapon, module and
carryable lists equally safe for any reader that sends one, and gives every client the same
catalog order to show.

The guarantee holds only while every runtime entry in the list arrives through the ordered
insert, which means every mod that adds to these lists publishes through the registry. A mod
that appends to a list itself puts an entry in the tail whose position depends on when it
ran, and from then on the position of every runtime entry in that list depends on timing: one
client can hold the self-appended entry before a published one and another after it. The
migration recipe below moves such a mod onto Publish.

## Crafting data

A carryable needs crafting rules, and Publish refuses one without them before anything is
registered, with one error naming the GUID. The game's end-of-run alloy tally walks every
carryable on the player ship and reads the crafting rules of each one's crafting def with no
null check, so a carryable without a crafting def throws there; the tally runs on the host
at session start and while a run's rewards are computed. The recycle tab also refuses an item
the crafting container does not know.

A weapon module needs them too: recycling and blank-box inscription read the crafting rules
under the weapon's GUID, and the alloy tally values an installed weapon by them before it
falls back to its build box.

Setting the rules: a printed item uses `CraftMethod.Resource` with an alloy `Creation` cost; a
weapon is inscribed onto a blank data box with `CraftMethod.ItemExchange` and a
`CraftableWithItemExchange(ItemExchangeType.BlankBox, minimumTier)`. Never combine the two
flags: the print check compares the method for equality with `Resource`. Recycling always
pays `Recycle.Amount` alloys.

## Components

`IsComponent = true` marks a carryable that is never offered as a loose item: an inner part of
another item, such as a payload inside a battery, or an item only the mod's own fabricator
entry hands out (see [Fabricator-only items](#fabricator-only-items)). Only a carryable can be
one; Publish refuses a weapon module or module marked as a component,
naming the GUID. A weapon module missing from its authored list could not spawn its build
box, because the game finds the weapon's def by scanning that list, and a module has no
inner-part role.

A component is registered in every container, so it spawns and resolves like any carryable,
but it is not listed in its container's authored list, so catalogs never offer it as a loose
item. It never reaches a loot roll: Publish refuses a component whose GUID is already
registered with `LootTables`, and `LootTables.Register` refuses a component from the moment
its publication is accepted, before registration completes.

A component still needs crafting data. It is not listed, but it is still a carryable on the
ship inside its parent, and the alloy tally reads it there. Publish refuses a component
without crafting data exactly as it refuses any carryable, and the framework's crafting-data
audit treats a component like any other carryable. A `Recycle` of 0 alloys is a valid answer
for a part that should be worth nothing on its own.

## Fabricator-only items

A carryable the crew can print at the fabricator but that no catalog offers, such as a
player-versus-player tool, publishes with `IsComponent = true`. The fabricator never reads the
carryable container's authored list, so leaving the item out of it costs the fabricator
nothing; `IsComponent` also keeps the item out of loot rolls and tells a `ModContent` reader not
to offer it.

What the fabricator does read, all of it registered by Publish whether or not the item is
listed:

- **Its shop.** Each fabricator terminal builds a shop (`CraftData`) when it is created and
  again on every fabricator tier change. The shop starts from the tier's authored craftable
  list, or, for a tier without one, from the crafting container's authored list, which Publish
  never writes. An entry is then added per GUID by `CraftData.TryAddItem`, which resolves the
  crafting def by GUID (runtime entries included) and refuses a GUID without one or with
  `CraftMethod.None`. A published item is therefore in no fabricator's shop until something
  adds its entry.
- **Its tabs.** Each entry takes its tab from the purchase sub-category on the carryable def.
  The shop sorts entries into six collections, keyed `AnimusCrates_Offensive`,
  `AnimusCrates_Defensive`, the value 23, `Resources_Materials`, `Mods_Offensive` and
  `Mods_Relic`, and puts an entry in the first collection whose key contains every bit of the
  entry's category (`key & category == category`). `Resources_Ammo` and `Resources_Support` fall
  in the materials collection. `Other_Basic` (Publish's default), `Special_Quest` and
  `Special_Unique` fall in none: the entry exists but no tab shows it. Set `ItemCategory`.
- **The print.** Printing needs `CraftMethod.Resource` and enough alloys. The request goes to
  every client; the fabricator's controller resolves the carryable def by GUID, looks the GUID
  up in its own terminal's shop, does nothing when the entry is missing or its method is
  `None`, and otherwise spawns the item through the object factory's spawn by GUID, which the
  runtime asset register and spawnable-object container answer.

So the mod adds the entry itself, on every client. Two routes put it there:

1. **The shared unlock pool.** On the host, `FabricatorModule.InitializeSharedUnlockPool`
   with the item's GUID, on each ship's fabricator module that should offer it. The game sends
   it to every client in a buffered call, and every terminal of that fabricator adds the entry
   through `TryAddItem`, now and each time it rebuilds its shop; a player who joins later
   receives the buffered call. The entry is flagged `SharedSessionUnlocked`, as a blueprint
   learned by recycling is. The pool is copied into the ship state the game carries from one
   session of a run to the next and re-sent from there, so the entry lasts the run. `TryAddItem` refuses every GUID on a tier whose list sets
   `OnlyOfferDataListItems`.
2. **A postfix on the `FabricatorData` constructor**, the object that owns a terminal's shop,
   on every client. Call `CraftingData.TryAddItem(guid)`. On a tier whose list sets
   `OnlyOfferDataListItems`, add the entry through the shop's private `AddCraftableAsPrintable`
   instead, add the GUID to the shop's `SessionUnlockedItems`, set the entry's `IsUnlocked`, and
   call the private `SetupCategories` and then `OnShopUpdated`. Nothing is carried into the
   next session; the patch decides per shop whether to offer the item, so it suits an item
   offered only in some matches.

Things that look like they should work and do not:

- **Listing the item to make it craftable.** No fabricator path reads the carryable
  container's authored list. Clearing `IsComponent` changes nothing at the fabricator and puts
  the item in every catalog that reads the list, including ship loadout editors that can then
  stock it for free.
- **Adding the entry only on the client that presses print.** The controller checks its own
  shop and drops the request without a message.
- **Setting `IsUnlocked` on an entry without adding its GUID to `SessionUnlockedItems`.** The
  terminal re-reads every entry's unlock state each time its panel activates, and an item
  outside `SessionUnlockedItems` takes it from the unlock container, which reads `Session`
  criteria as locked. The entry turns grey and cannot be selected the first time a player
  walks up to the terminal.
- **`Unlock = Always` to make the entry show unlocked.** It writes the GUID into every
  player's persistent unlocks, where it stays after the mod is removed, and the shop still
  needs the entry added.
- **Leaving `ItemCategory` null.** The entry is in the shop under `Other_Basic`, which no tab
  shows.

## Timing and the handle

Publish is synchronous once the game's asset containers have loaded. Before that (a call
from a plugin's `Awake`, for example), the publication is queued as `Pending`
and the framework's registration driver completes it within two seconds of the containers
loading. A failed attempt stays `Pending` and is retried on each pass; it is logged on the
first attempt and every tenth, and given up as `Failed` after 200. A refusal is `Failed` at
once and never retried. `Reason` holds the refusal, or the last attempt's error while
`Pending`.

Publishing the same GUID again from the same owner, as the same kind, returns the first
handle, whether that publication was accepted or refused, and a refusal is logged once. A
mod's own registration loop may therefore call Publish on every pass. The same GUID from
another owner is refused.

Call Publish from the main thread, as Unity requires for the objects it registers. Nothing
guards a call from another thread.

Publish before the player first opens a terminal. A catalog that builds its item lists once
per session only sees what was registered by then. A catalog that binds `ModContent.Changed`
rebuilds when an item arrives.

`RuntimeContent.Status` counts publications by state. The F5 page shows it.

## `ModContent`: reading published items

Every item that reached `Registered` has an entry in `ModContent`:

| Member | Meaning |
| --- | --- |
| `ApiVersion` | `1`. Raised only when a member is removed or changes meaning. |
| `TryGet(GUIDUnion, out ModContentEntry)` | A copy of one item's entry. |
| `All` | Fresh copies of every entry on each read, ordered by GUID. |
| `IsAvailable(GUIDUnion)` | False only when the GUID has an entry whose `AvailableInRoom` returns false or throws. A GUID with no entry is not restricted. |
| `Changed` | Raised on the main thread after an entry is added. |

| `ModContentEntry` field | Meaning |
| --- | --- |
| `Guid` | The item's GUID. Set by Publish. |
| `Kind` | `"WeaponModule"`, `"Module"` or `"Carryable"`. Set by Publish. |
| `DisplayName` | The name shown, also the header of the item's context info. Required. |
| `Owner` | The publishing plugin's GUID. Required. |
| `Category` | A tab hint. Empty becomes the game's category name: the purchase sub-category for a carryable, the module category for a module, `"Weapons"` for a weapon module. |
| `Tier` | 1 to 3 for Mk I to Mk III, independent of the display language. 0 is unranked. |
| `IsComponent` | Never offered as a loose item: never listed, never in a loot roll on its own. An inner part, or an item only its mod's fabricator entry hands out. |
| `PvpLegal` | False keeps the item out of player-versus-player matches for a mod that runs them. Default true. |
| `AvailableInRoom` | `Func<bool>`, the same answer on every client of a room. Null at publish is stored as always-true. A role-owned item passes `() => RoleRegistry.IsAvailableInRoom(roleId)`. |
| `IconHint` | Free text for a catalog that draws its own icons. |

Every field is a BCL type or the game's `GUIDUnion`, so a mod can read the registry without
referencing the framework:

```csharp
Type t = Type.GetType("Ectype.ModContent, EctypeFramework");
if (t != null && (int)t.GetField("ApiVersion").GetValue(null) == 1)
{
    object[] args = { guid, null };
    if ((bool)t.GetMethod("TryGet").Invoke(null, args))
    {
        object e = args[1];
        int tier = (int)e.GetType().GetField("Tier").GetValue(e);
        bool component = (bool)e.GetType().GetField("IsComponent").GetValue(e);
    }
    EventInfo changed = t.GetEvent("Changed");
    changed.AddEventHandler(null, (Action)RebuildLists);
}
```

Read members one by one and treat a missing one as absent: later versions only add fields.

`LootTables` reads the registry too. `Register` refuses an item accepted for publication as a
component, and a roll leaves out an item whose entry is a component or not available in the
room.

## Migrating a weapon mod that registers its own containers

For a mod that registers a composite weapon by hand and appends its def to the authored list
itself. The migrated mod requires framework 0.27 or later: raise its dependency on the
framework to that minimum, `[BepInDependency("jack.voidcrew.ectype", "0.27.0")]`. On an older framework, the method that calls Publish
fails to compile at its first call with a `MissingMethodException`, which takes the mod's
whole registration with it. Per tier:

1. Keep everything up to and including the finished `CompositeWeaponData`: the cloned stat
   descriptors, their runtime-asset and descriptor-container registrations, and the weapon's
   element and stat graph.
2. Delete the mod's registration of the weapon's crafting def, weapon def, unlock def and
   runtime asset, and the append to the weapon container's authored list. Publish refuses a
   GUID one of those containers already holds or the authored list already lists, so these
   must go, not merely be skipped on retry. A runtime asset left registered is accepted, but
   then keeps the effect the mod gave it.
3. Call Publish with `ContentKind.WeaponModule`:
   - `Asset`: the `CompositeWeaponData`;
   - `Description`, `Icon` and `BuildBoxIcon`: what the mod put into its context info and
     build-box icon;
   - `Crafting`: the same rules it registered, for example `ItemExchange` onto a blank box
     with a minimum tier;
   - `Rarity`: the tier's rarity;
   - `Unlock`: the criteria it used, normally `Session`;
   - `ItemCategory`: the donor weapon def's category;
   - `ModFlag`: the effect it registered the weapon with;
   - the entry's `DisplayName`, `Owner` (the plugin GUID) and `Tier` (1, 2 or 3).

   Repeated calls return the same handle, so the call can sit in the mod's existing retry
   loop.
4. Where the mod used its own def afterwards (to spawn a build box, for example), read it
   back from the weapon container by GUID once the handle is `Registered`.
5. Keep the loot registration. Keep the upgrade-chain entry, but write it only once every
   tier's handle is `Registered`, since it references all of their GUIDs.
6. Keep the network deserialization patch for the weapon's runtime ref, and any unlock-check
   patch for `Session` items.

An inner part the weapon carries as a loose carryable, such as a payload, publishes as a
`Carryable` with `IsComponent = true` and crafting data.

The check, per tier: one `registered (weapon, crafting and unlock containers; listed)` line;
the weapon's build box spawns with no `cant find <guid>` error from the weapon container; the
fabricator recycles it for the expected alloys; and a catalog that reads the weapon
container's authored list shows it without change on its side.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: content: <kind> <guid> from <owner> '<name>' registered (<containers>; listed).` | Registered; `component, not listed` for a component. |
| `Ectype: content: refused <kind> <guid> from <owner>: <reason>` | Refused; nothing more happens to it this session. The reasons are in [troubleshooting](../troubleshooting.md#content-registry). |
| `Ectype: content: <kind> <guid> from <owner> attempt <n> failed at [<stage>]: <message>` | An attempt failed and will be retried; logged on attempt 1 and every tenth. |
| `Ectype: content: giving up on <kind> <guid> from <owner>. Last error: <exception>` | 200 attempts failed; the handle is `Failed`. |
| `Ectype: content: publication pass failed: <exception>` | The driver's pass over queued publications threw. |
| `Ectype: content: ModContent.Changed subscriber <type>.<method> threw: <exception>` | A `Changed` handler threw; the others still ran. |

Sample: examples/content-registry/
