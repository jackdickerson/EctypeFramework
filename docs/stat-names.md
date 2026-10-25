# Stat names for Ectype manifests

The alias list is [stat-names.csv](stat-names.csv) (columns `Name,Id,Category,Type,Source`):
every alias a manifest effect line can put in its `"stat"` field, extracted from the game's
`StatType` enumeration. The framework enumerates the same source at runtime, so the names
available in game track whatever the installed game ships. The CSV is a snapshot of one game
build; the category derives from the id's category-flag bits, `(id & 0x0FFFFFFF) >> 20`.

`Type` is the value type of the stat object the game registers under the id. The id says it:
`StatType.INT_FLAG` (`0x10000000`) is set on every `ModifiableInt` stat and on no float one.
`int` rows are whole numbers. `bool` rows are ints the game only reads as on/off
(`Value > 0`). `pip-float` rows are `PipModifiableFloat`s whose base value comes from a pip
curve (their `*Pip` sibling is the int). `unused` means no code registers the id at all. A
`pip-float` id is also registered as a plain float on other owners (turrets, NPC abilities),
so the type describes the composite weapon module.

`Source` is `vanilla` for a `StatType` member and `framework` for a stat the framework
declares. Framework rows carry the category of the collection they live on.

Names are matched case-insensitively. A raw int id is accepted as an escape hatch. Custom
stats declared in the same manifest's `customStats` are also valid and shadow these names for
that role's own effect lines, so do not reuse a vanilla name. Stat names from other mods can
be used too, provided those mods are marked required. A stat a mod declares in its stats file is
named `"owner/Name"`, for example `"fleet/HackSeconds"`; such stats are not rows of the CSV, and
the declaring mod's own documentation lists them (see
[reference/mod-stats.md](reference/mod-stats.md)).

## Resolution order

An effect's `"stat"` field resolves in this order: the role's own `customStats`, then the
framework-owned names, then an `"owner/Name"` a mod's stats file declares, then `StatType`, then
a raw int. A role's own declarations win so that
a name the framework adds later can never break an already-shipped role. The framework's
names come before `StatType` for the same reason in reverse. A framework-owned name that
shadows a vanilla one is a framework bug, caught at registration by the `StatTable` collision
check.

## Framework-owned stats

Rows with `Source` `framework` are not from `StatType`. They are stats the framework itself
declares and reads, for numbers the game gives every player but left as plain fields with no
modifier surface:

- The grappling hook's flight speed, pull speed, pull acceleration, miss cooldown and a
  hook-scoped cooldown alias.
- Five character movement numbers on the player's own collection (`ExternalForceDamping`,
  `ExternalForceDampingTime`, `MotorDamping`, `MotorAcceleration`, `GravityAmount`), so a
  `character`-target perk reaches them.
- `SystemModuleConstructionSpeed`, a factor on install, uninstall and upgrade rate, on the player's own
  collection.
- `IncomingProjectileSpeed` on each player ship's collection: a factor (authored 1) on the
  speed of hostile NPC projectiles aimed at that ship, applied to each shot as it registers.
  Below 1 slows enemy shots in flight.
- `ShipDrag` (the platform's quadratic drag) on the player ship's collection.
- Generic factors (authored 1) that the framework registers and a mod reads by id:
  `MeleeDamage`, `MeleeCooldown` and `MeleeReach` on the player's own collection,
  `AntiPersonnelDamage` on each player ship's collection, and `HostileSpawnScale` on the
  session collection. Their category `session` names the framework's run-wide collection,
  which no vanilla stat lives on and no perk target reaches: a manifest effect naming
  `HostileSpawnScale` is refused.
- A hand-held attractor's `HandAttractorMaxRange`, `HandAttractorPullVelocity`,
  `HandAttractorDuration`, `HandAttractorCooldown` and `HandAttractorBeams` on the owning
  ability's collection. Duration and cooldown are second ids onto the ability's own
  `ClassAbilityDuration` / `ClassAbilityCooldown` floats, scoped to the hand. Range and pull
  velocity mirror the vanilla `AttractorMaxRange` / `AttractorPullVelocity`: each hand float
  is registered under two ids, the framework id and the vanilla id, so a character-side
  modifier on either id reaches it. One source must never name both ids, or the two
  modifiers compound. A modifier applied to a ship's collection (`seatShip`,
  `characterShip`, `characterAboard`, `characterFleet`, `seatFleet`, ship-installed items) never
  reaches the hand.

[framework-stats.json](framework-stats.json) describes each one: what it wraps, the game's
authored value, and how the game consumes it. [reference/framework-stats.md](reference/framework-stats.md)
explains the mechanisms. Any manifest may name one without declaring anything. Unlike a
role's own `customStats` they are not role-scoped, and unlike them they already have a
consumer.

The ids are hashes of the framework's reserved owner string and the name. The framework logs
them on the `framework-owned stats registered` line at startup. The names and the owner string
are part of the contract across a room: renaming one derives a different id, which is a silent
no-op on the side of a mixed room that has the older build.

## The category guides the perk's `target`, as a heuristic

A stat is read from the collection it lives on. `weapon/module` and `ship *` stats generally
want `seat` or one of the ship targets (`seatShip`, `characterShip`, `characterAboard`,
`characterFleet`, `seatFleet`, which all land on ship collections), and `EVA suit` and
`character` stats want `character`. Some stats
exist on more than one collection: vanilla ships `*Vulnerability` and `BatteryRechargeAmount`
on `character`-target perks (personal damage resistance, carried-cell charging) despite their
ship-side category bits. The vanilla dump a dev tools build of the framework
([CONTRIBUTING.md](../CONTRIBUTING.md#dev-tools-build)) writes to
`BepInEx\EctypeDump\vanilla-roles.json` with the `[Dev] WriteVanillaDump` switch on is the
authority: when it shows a stat on a target, that pairing works. A stat routed to a collection that never reads
it registers cleanly, renders a tooltip, and does nothing.

## `*Pip` variants

Avoid the `*Pip` variants unless you know the pip system. They target the pip *curve* of an
upgradeable weapon stat, not a value.

## Unverified semantics

Semantics beyond name, id and category are unverified. Units, scales (0-1 versus 0-100), and
sign conventions per stat have not been tested. Treat any load-bearing number as needing an
in-game check, and record what you assumed in your mod's README.
