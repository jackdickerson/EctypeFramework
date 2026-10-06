# Changelog

Newest first. Each entry lists what changed for a mod that depends on the framework.
A version that is not listed carried no change a dependent can observe, or has no recorded
build.

## 0.19.11

- Breaking: "class" is renamed "role" throughout, matching the game's own code (EPlayerRole). Manifest
  contract version 2. A contract 1 manifest does not load: under its old resource name it is
  not discovered, and renamed alone it is rejected for its `contractVersion`. Rename its
  fields, its resource and its `contractVersion`, then rebuild.
- Public API renames. Behaviour is unchanged.
  - `ClassRegistry` is `RoleRegistry`. `RegisterAbilityFactory(roleId, factory)`,
    `RegisterUnlockPredicate(roleId, predicate)`, `PerkGuid(roleId, perkKey)` and
    `IsAvailableInRoom(roleId)` keep their signatures; the first parameter is `roleId`.
  - `EctypeClassAbility` is `EctypeRoleAbility`.
  - `ClassStats` is `RoleStats`: `Id(roleId, statName)`,
    `RegisterAbilityTiming(ability, roleId, durationStat, cooldownStat)`.
  - `ClassManifest` is `RoleManifest`.
  - `LayoutSpec` (the layout workbench file): `showClassName`, `classNameFontSize`,
    `classNameColorHex`, `classNameTopPx` and `classNameHeightPx` are `showRoleName`,
    `roleNameFontSize`, `roleNameColorHex`, `roleNameTopPx` and `roleNameHeightPx`.
- Manifest fields: `classId` is `roleId`, `className` is `roleName`, `classDesc` is
  `roleDesc`, `suppressClasses` is `suppressRoles`. GUIDs, tree ordinals and stat ids derive
  from the id's value, not the field's name, so a role whose id string is unchanged keeps its
  saved perk selections.
- The manifest resource suffix is `ectype-role.json` (was `ectype-class.json`); a resource
  with the old suffix is not discovered. The schema is `docs/schemas/ectype-role.schema.json`
  and rejects the four contract 1 field names.
- Config: the `[ClassSuppression]` section is `[RoleSuppression]`, with keys
  `Suppress_<roleId>`. Values under the old section are not migrated: a host that had a role
  switched off sets the toggle again.
- The vanilla tree dump is written to `BepInEx\EctypeDump\vanilla-roles.json` (was
  `vanilla-classes.json`).
- Log lines say role where they said class, for example `Ectype: role '<name>' registered.`
  and `Ectype: discovered <n> role(s)`. Log filters keyed on the old wording need updating.
- The starter examples are `examples/data-only-role` and `examples/ability-role`.

## 0.19.10

- The framework stat `ModuleInstallSpeed` is renamed `SystemModuleConstructionSpeed`. Its id
  derives from its name, so it changes from 592760197 to 761402164; a perk or manifest that
  names the old name no longer resolves and must be rewritten. Behaviour is unchanged: the
  stat still scales install, uninstall and upgrade, because the game runs all three on one
  progress clock.

## 0.19.9

- No API or behaviour change. The framework now builds from its own repository against the
  game install, which changes the DLL's bytes. Every player in a room needs this build, and a
  dependent rebuilt against it records the new assembly version.

## 0.19.8

- `wide` is a validated layout field. A perk on a full-width cell (rows 2, 5 and 7 of the
  grid) must declare `"wide": true`, and `"wide": true` on any other row rejects the class.
  Absent means false. The hero is always row 0 and needs no flag. A manifest that omitted
  `wide` on a banner row, or set it on a normal cell, is now refused with a named error.

## 0.19.7

- New APIs: `SelectionGate` (host-side check that every room member runs a mod at the
  host's build, with a verdict and per-member states), `EnemyAbilities`, `LootTables`
  (add runtime-registered items to the game's loot rolls), `ShipArrivals` (an event on
  every machine when the ship leaves the void), `TimedStatContributions` (a modifier that
  follows a curve over a duration) and `ShipSpeedSurge`.
- New framework stats: `ShipDrag` (the ship platform's friction) and `ModuleInstallSpeed`
  (how fast a player installs, uninstalls and upgrades modules).
- A field-backed stat now tracks the stat after later modifier changes.

## 0.18.8

- `roleIcon` in the manifest is the glyph the crew HUD and the lobby room list draw
  beside a player of the class. A class with no `roleIcon` shows the framework
  placeholder there.

## 0.18.7

- New `ClassStats` API: `Id(classId, statName)` resolves a manifest-declared custom stat,
  and `RegisterAbilityTiming` registers one class ability's own duration and cooldown so a
  perk can be scoped to that ability.
- A perk level the class no longer defines is clamped on load, and the points spent on it
  are refunded.
- A suppressed tree's fabricator unlock can be adopted by any class that suppresses the
  tree, not only the first.

## 0.18.5

- The fallback for a perk or class icon with no embedded PNG and no `iconDonor` is the
  framework's embedded placeholder (square, or wide for the row-2 banner). It was a
  vanilla icon.

## 0.18.4

- Hand attractor duration, cooldown and beam-count stats.

## 0.18.3

- The galaxy-map mutator list skips mutator GUIDs that no loaded mod registers, so a
  client without a host's mutator mod no longer fails on that list.

## 0.18.2

- Hand attractor stats. The `VerboseLogging` config key turns on per-character ability and
  HUD bar logging.

## 0.18.1

- New perk `target` value `characterShip`: the effects go to the ship's stat collection
  for every holder of the perk, seated or not, and reach ship module trees the game
  detaches from the ship, such as the Fabricator's.
- New manifest field `minFrameworkVersion`. A framework older than the value rejects the
  class by name.
- A suppressed tree is reported absent to the profile (spent points, ability selection,
  fabricator unlocks), and already-built gene-tree screens rebuild when the suppressed set
  changes.

## 0.17.0

- New `CharacterStatuses` API: timed, named stat bundles on a player, replicated to every
  machine.
- New `HudAbilityBar` API: extra buttons on the class ability bar.
- New manifest field `suppressClasses`: a class suppresses another framework class by
  `classId`.
- New ship stat `IncomingProjectileSpeed`.
- Builds reporting 0.16.2 between 2026-09-12 and 2026-09-14 differ in which of these they
  contain. Require 0.17.0 or newer for any of them.

## 0.16.2

- `ClassRegistry.PerkGuid(classId, perkKey)` is public: class code resolves its own perk
  GUIDs without pinning literals.
- A class ability's HUD slot paints correctly on first attach and hides the previous
  class's ability element.

## 0.13.0

- New `EnemyStatuses` API: timed buffs and debuffs on NPC ships, turrets and other space
  objects, with an optional attached visual.

## 0.10.0

- The DLL is `EctypeFramework.dll`. It was `EctypeClassFramework.dll`. The plugin GUID
  `jack.voidcrew.ectype` and the manifest contract are unchanged. A dependent that
  references the assembly must be rebuilt against the new name, and the old DLL must be
  removed from `BepInEx\plugins\`: two copies register the same GUID.
- A host-only mutator no longer throws for a client that has the framework but not that
  mutator: the framework drops an active mutator GUID the client has no definition for.

## 0.9.1

- New `CharacterStats` and `FieldStat`: a raw game field wrapped as a framework stat, so
  perks and abilities contribute through the game's modifier composition. First stat
  added: `ExternalForceDamping`.

## 0.8.0

- New `RuntimeAbilities` API: replicates the active state of a runtime-registered ability
  to the other clients.

## 0.7.1

- New framework-owned stat registry (`FrameworkStats`), including the grapple hook stats.
- Host-side per-class suppression toggles and room-property sync, so every client applies
  the same suppressed set.

## 0.6.16

- A class that suppresses a vanilla tree takes over the tree's class ability and
  fabricator unlocks: the ability remains selectable and the unlocks stay reachable.
- New perk field `iconDonor`, and class field `roleIconDonor`: borrow the icon of any
  perk by GUID.

## 0.6.2

- New effect fields `requiredTags`, `requiredTagCriteria`, `requiredLocalTags`,
  `requiredLocalTagCriteria`, `disablingTags`, `tagsToAdd`: tag filters on a stat effect.
- New effect field `kind`: `"int"` builds an integer modifier.
- New perk field `unlocks`: Fabricator items the perk grants once bought.
- New perk field `emptyOk`: declares an empty level intentional.

## 0.6.1

- New `ClassRegistry.RegisterAbilityFactory`: a class mod supplies its hero perk's class
  ability in code.

## 0.5.10

- First recorded build. Manifest contract version 1: class manifest discovery by embedded
  resource, tree registration, vanilla tree suppression, tree presentation, and the
  stat registry.
