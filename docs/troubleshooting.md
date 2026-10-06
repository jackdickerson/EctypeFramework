# Troubleshooting

Every entry is keyed by text the framework writes to `BepInEx/LogOutput.log` (the log is
overwritten on each launch, so copy it before relaunching) or shows on the F5 settings page
under Ectype Framework. `<...>` marks a value that varies. Each entry gives what the line
means, what causes it and what to do.

Lines tagged `[Warning]` and `[Error]` always appear. Lines that report a per-character or
per-open event appear only with `[Debug] VerboseLogging = true` in
`BepInEx/config/jack.voidcrew.ectype.cfg`. Turn it on before reproducing a problem.

## The F5 status page

| Line | Value | Meaning |
| --- | --- | --- |
| `Pipeline:` | `waiting for asset containers` | The game's perk, tree, stat, tag and unlock tables are not loaded yet. Registration retries every 2 seconds. Stays here only if the game never reaches the point that loads them. |
| | `inert: patching did not complete` | Harmony patching failed. See the `FATAL` entry under Load. |
| | `no class manifests found` | No installed mod carries a manifest. See "No class appears". |
| | `<n>/<m> classes registered` | `n` of `m` discovered classes finished registering. `(<k> failed)` is appended when `k` gave up. |
| `Stats:` | `<n> vanilla aliases, framework-owned <status>` | The stat names a manifest can use. `not registered` means the stat table has not loaded; `<n> registered` is the normal state. |
| `Suppress:` | `not resolved yet`, `nothing to suppress`, `no trees suppressed`, or `<n> tree(s) suppressed: <names>` | Which stock gene trees class mods have removed. `(<n> ignored)` counts manifest entries that named nothing usable; each has its own warning. |
| `Host suppress:` | `nothing host-suppressed` or `<n> class(es) host-suppressed: <classIds>` | Classes the host has switched off for the room. |
| One line per class | `registered (stage limit 9); tree def published as EPerkTree <n>` | The class is registered and its tree is published. |
| | `waiting for asset containers` | Same as the pipeline line, for this class. |
| | `attempt <n> failed at [<stage>]: <message>` | Registration threw. See "Registration attempt failed". |

The class line also shows the tree number and the assembly that supplied the manifest.

## Load

### `Ectype Framework v<version> loaded (contract v<n>).`

The framework started and applied every patch. `n` is the manifest contract version this build
accepts. No class is registered yet; look for `Ectype: discovered` next.

### `Ectype: disabled via config ([General] Enabled = false).`

The framework did nothing: no patches, no registration, no driver. Set `[General] Enabled` to
`true` in `jack.voidcrew.ectype.cfg` and restart. Mods that depend on the framework still load
and still declare their multiplayer type, but nothing they registered with the framework runs.

### `Ectype: FATAL - patching failed, framework is inert this session and no classes register: <exception>`

A Harmony patch could not be applied. Harmony does not undo patches applied before the failure,
so the framework treats a partial set as unusable: it starts no driver and every registration
entry point refuses to run. The F5 pipeline line reads `inert: patching did not complete`.

The usual cause is a game update that renamed or reshaped a patched method. The exception names
it. Install a framework build made for the installed game version. If one does not exist, report
the exception text.

### `Ectype: binding the selection gate failed: <exception>`

The framework could not subscribe to VoidManager's room events. `SelectionGate` will not
re-evaluate on its own. The usual cause is a VoidManager version with a different event surface.
Install the VoidManager version the framework was built against.

### `Ectype: enemy ability gates failed: <exception>` and `Ectype: loot table patch failed: <exception>`

The enemy-ability suspension patches or the loot-table patch could not be resolved. Each system
is patched on its own, so a renamed game member disables only that system. Class registration
and the other utilities are unaffected. Cause and fix are the same as for `FATAL`.

### A dependent calls the framework and gets a `NullReferenceException`

The framework assigns its logger in its own `Awake`. A dependent that calls a framework API
(`ClassRegistry.RegisterAbilityFactory`, for one) before that `Awake` has run fails inside the
call. Declare `[BepInDependency("jack.voidcrew.ectype")]` on the dependent's plugin class.
BepInEx then runs the framework's `Awake` first. Without the attribute, load order is not
defined.

## Manifest validation

A manifest is an embedded resource whose name ends in `ectype-class.json`. The framework scans
every loaded plugin assembly once, at the first registration pass, and logs
`Ectype: discovered <n> class(es): <summary>.` when the scan ends.

### `Ectype: manifest '<resource>' in <assembly> rejected: <problem>`

The manifest parsed as JSON and failed a check. The class is skipped and nothing else is
affected. `<problem>` is one of:

| Problem | Cause and fix |
| --- | --- |
| `empty document` | The resource is empty or `null`. |
| `needs Ectype Framework <x> or newer (this is <y>)` | `minFrameworkVersion` is newer than the installed framework. Update the framework, or lower the field if the class uses nothing newer. |
| `minFrameworkVersion '<x>' is not a version (expected "major.minor.patch")` | Write the field as three dot-separated numbers. |
| `contractVersion <n> (this framework speaks 1)` | The manifest declares a contract this framework does not read. Set `contractVersion` to the value the framework prints in its load line. |
| `no classId`, `no className` | A required field is missing or empty. |
| `no hero perk` | `hero` is missing. Every class needs a row-0 hero perk. |
| `null perk entry` | The `perks` array holds a `null`. |
| `a perk has no key` | Every perk needs a `key`. |
| `duplicate perk key '<k>'` | Keys are compared without regard to case and must be unique within a class. The hero's key counts (it defaults to `hero`). |
| `perk '<k>' has no name` | Every perk needs a `name`. |
| `perk '<k>' has row <n> (row 0 is the hero banner)` | Perk rows start at 1. |
| `perk '<k>' on row <n> is a wide cell and must declare wide: true` | The perk sits on a full-width cell (row 2, 5 or 7) without `"wide": true`. Add the flag. |
| `perk '<k>' on row <n> has wide: true, but only rows 2, 5 and 7 are wide cells` | `"wide": true` on a normal cell. Remove the flag, or move the perk to a wide row. |
| `perk '<k>' unlocks entry '<x>' is not a GUID (32 hex chars, dashes allowed)`, `hero unlocks entry ...`, `suppress entry ...` | A GUID list entry is not 32 hexadecimal characters. Dashes are accepted. |
| `hero perk has unrecognised target '<x>' (this framework knows character, seat, seatShip, characterShip)` | The hero's `target` is misspelled or newer than this framework. A bad target on any other perk skips only that perk. |
| `a custom stat has no name` | Every `customStats` entry needs a `name`. |

### `Ectype: manifest '<resource>' in <assembly> failed to parse: <message>`

The resource is not valid JSON for the manifest model. The message comes from the JSON reader
and usually names the line or the field whose value has the wrong type (a string where a number
belongs, for instance). Fix the file. Check for a byte-order mark or a trailing comma.

### `Ectype: class '<name>' perk '<key>' skipped: unrecognised target '<x>' (this framework knows character, seat, seatShip, characterShip).`

A non-hero perk names a `target` the framework does not know. Only that perk is dropped. Fix the
spelling; the comparison ignores case.

### No class appears and the log says `Ectype: discovered 0 class(es).`

The framework found no manifest. The discovery rule is that an embedded resource name ends with
`ectype-class.json`, compared without regard to case. Check that the file is marked as an
embedded resource in the project and that its resource name (the assembly's root namespace and
folder path, joined with dots, unless the project sets a logical name) ends with that suffix.
Check also that the plugin loaded: a plugin that failed to load never has its assembly scanned.
The framework scans assemblies of loaded BepInEx plugins only.

### `Ectype: duplicate classId '<id>' in <assembly> (already provided by <assembly>) - the duplicate is skipped.`

Two installed mods declare the same `classId`. The first one the scan met wins. Rename the
`classId` in one of them. The id seeds every GUID of the class, so renaming it after release
orphans saved selections; pick a unique id before releasing.

### `Ectype: discovery requested after registration closed (first terminal build) - refused.`

The first gene-tree terminal was built before discovery ran. The ordinal each class receives
depends on the full set of classes, so a class that appeared later would get a different
ordinal on different clients. Discovery is refused instead. Restart the game. If it recurs on
every launch, the framework's registration is not reaching the point where it runs; check the
log for an earlier `FATAL` or `Pipeline` line.

### Class colour warnings

| Line | Meaning |
| --- | --- |
| `Ectype: class '<name>' declares no color - its tree will render untinted (white).` | `color` is missing. |
| `Ectype: class '<name>' color '<c>' is not parseable (expected "#RRGGBB") - the tree will render untinted (white).` | Use a six-digit hex string with a leading `#`. |
| `Ectype: class '<name>' color <c> is too close to white (R+G+B = <n> >= 700) - it will be hard to tell from an untinted tree.` | The red, green and blue values sum to 700 or more of 765. Pick a darker colour. |
| `Ectype: classes '<a>' (<c>) and '<b>' (<c>) have nearly identical accent colors (distance <n> < 90) - the trees will be hard to tell apart.` | The red, green and blue differences between two installed classes sum to less than 90 of 765. Change one. The stock classes use red, blue, green and purple. |

### Suppression warnings

All are logged when the framework resolves what a class mod suppresses. None fails a class.

| Line | Cause |
| --- | --- |
| `Ectype: suppress entry <guid> (from '<classId>') names a framework-registered class asset - name its classId in suppressClasses instead. Entry ignored.` | `suppress` takes stock tree GUIDs. Move the class to `suppressClasses`. |
| `Ectype: suppress entry <guid> (from '<classId>') is a tree whose def or asset did not resolve - entry ignored.` | The GUID names a tree whose data did not load. Check the GUID. |
| `Ectype: suppress entry <guid> (from '<classId>') names a single perk; only whole trees can be suppressed. Entry ignored.` | A perk GUID is not a tree GUID. |
| `Ectype: suppress entry <guid> (from '<classId>') resolves to nothing in this game version - entry ignored, nothing suppressed.` | The GUID does not exist in the installed game. |
| `Ectype: class '<id>' lists itself in suppressClasses - entry ignored.` | Remove the entry. |
| `Ectype: class '<id>' suppresses class '<other>', which is not installed - entry ignored.` | Harmless when the other class is optional. |
| `Ectype: suppressClasses cycle through '<id>' - treated as active.` | Class A lists B and B lists A. The cycle is broken in `classId` order. |

## Registration

Registration runs in stages for each class: `guids`, `custom stats`, `perk assets`, `runtime
assets`, `perk defs`, `tree asset`, `tree def`. It retries every 2 seconds until every stage
passes, because the game's tables load some time after the plugin's `Awake`.

### `Ectype: class '<name>' registered. Tree <guid> as EPerkTree <n>, <m> perk(s). <status>`

Success. `n` is the tree's ordinal, assigned by sorting `classId` values, so every client with
the same set of class mods gets the same ordinal.

### `Ectype VERIFY '<name>': ok. <n> perk(s) resolve; tree asset built; tree def published.`

The follow-up check after registration. Two failure forms exist:

- `Ectype VERIFY '<name>': perk '<key>' does not resolve by GUID.` The perk's asset did not
  enter the game's registry. Look for an earlier `registration` warning for the class.
- `Ectype VERIFY '<name>': perk '<key>' def.Asset is null - ref not IsRuntime, or the asset was collected.` The registered asset was released. Report it with the log.

`tree def not published` in the success line means the tree def stage was skipped: either
`[General] RegisterStage` is below 6, `[General] PublishTreeDefs` is `false`, or the tree
controller patch is missing (next entry). Reset those keys to their defaults, which are `9` and
`true`.

### `Ectype: '<name>' registration attempt <n> failed at [<stage>]: <message>`

A stage threw. The framework logs the first failure and every tenth after it, and tries again
after 2 seconds. After 200 attempts it gives up with
`Ectype: giving up on '<name>'. Last error: <exception>` and the class is absent for the
session. The message to read is the one after the colon. Common ones:

| Message | Cause and fix |
| --- | --- |
| `StatTable not loaded yet`, `CsTagTable not loaded yet` | A table the game had not loaded when the attempt ran. Transient; later attempts succeed. Reported only if it persists. |
| `custom stat id collision: <a> and <b> derive the same id <n>` | Two custom stats derive one id. Rename one stat. |
| `custom stat <owner> derived id <n> already present in StatTable` | A custom stat's id collides with an existing stat. Rename the stat. |
| `could not set PerkTreeDef.perkTree` | The game's `PerkTreeDef` no longer has the expected field. A game update. |

### `Ectype: '<name>' perk '<key>' level <n> effect skipped: <message>`

One effect in one level could not be built. The perk still registers, without that effect.
The message says why:

| Message | Fix |
| --- | --- |
| `unknown stat '<s>' (not a StatType name, not a framework-owned stat, not a custom stat of this class, not an int id)` | The stat name is misspelled or does not exist. Names are compared without regard to case. The list is in `docs/stat-names.md`. A stat declared by another mod cannot be named. |
| `unknown modifier type '<t>' (percent, scalar, flat, flat2, or a ModifierType name)` | Fix `type`. |
| `effect names no stat` | The effect has no `stat` field. |

### `Ectype: unknown effect kind '<k>' on stat '<s>' - using float.`

`kind` accepts `float` and `int`. Anything else builds a float modifier.

### `Ectype: <context>: tag '<name>' did not resolve (<message>) - dropped from this effect's filter.`

`requiredTags`, `disablingTags` or another tag list names a tag the game does not have. The
effect registers with a narrower filter than written, so it may apply in places you excluded.
Fix the tag name.

### `Ectype: unknown tag criteria '<c>' - using 'and'.`

`requiredTagCriteria` accepts `and` and `or`.

### `Ectype: '<name>' perk '<key>' level <n> has EMPTY StatMods - it costs a point and does nothing.`

A level has no effects. Either add effects, or set `"emptyOk": true` on the perk when the
payload is code (an ability perk) or an unlock. The warning is logged on every launch until one
of the two is done.

### `Ectype: perk '<key>' names iconDonor <guid>, which resolves to no perk icon - falling back to the placeholder.`

`iconDonor` is not the GUID of a registered perk container with an icon. The framework's
placeholder shows instead. Check the GUID against the game's perk list.

### `Ectype: embedded icon '<key>.png' not found in <assembly> - falling back.`

The manifest names an icon key with no matching embedded PNG. The icon resolves in this order:
the embedded PNG, the perk's `iconDonor`, the framework's placeholder. A resource matches when
its name ends with `<key>.png`, compared without regard to case, and the first match wins. Check
that the PNG is an embedded resource, and that no other resource's name ends with the same text.

### `Ectype: could not decode icon '<key>'.` and `Ectype: icon load '<key>' failed: <message>`

The resource exists but is not a readable PNG. Re-export it.

### `Ectype: '<name>' perk '<key>' unlocks item <guid>, which already has an UnlockItemDef from vanilla or another mod. Skipped: this perk grants no fabricator unlock this session.`

A perk's `unlocks` entry names an item that already has an unlock definition. The framework
cannot repoint an existing definition, so that perk grants nothing for this item. The perk
itself registers. Remove the entry or use an item with no unlock of its own.

### `Ectype: perk '<name>' (target <t>) has an effect on <stat> that matches no stat in <where> collection tree - it stays registered and changes nothing there.`

A perk's effect names a stat that the perk's `target` does not hold. A stat's category never
limits a target, so the effect registers and does nothing. The framework checks after the perk
reapply, and counts a miss only when a reapply at least 10 seconds later still misses, because
modules and abilities join the stat tree after spawn. Change the `target`, or change the stat.

### `Ectype: the PerkTreeController ctor prefix is not applied - tree defs are not published this session. Classes register but no tree renders.`

The patch that injects class trees into the terminal did not apply. The framework withholds
every tree def rather than let the terminal throw. Cause is the same as for `FATAL`.

### `Ectype: dropped perk GUID <guid> before ReApplyPerks (suppressed)` or `(unresolvable - would have thrown and wiped this player's perks)`

A player's saved or replicated perk list holds a GUID that no longer resolves. This happens
when a class mod is uninstalled, when a class's `classId` or a perk's `key` changed, or when
another player has a class this client lacks. The game's perk reapply would have thrown and
left the player with no perks, so the framework removes the entry first. The save keeps the
entry; reinstalling the mod restores the perk. `(suppressed)` is the same drop for a perk of a
suppressed class.

### `Ectype: perk GUID <guid> held at level <n> but has <m> level(s) - applying level <m> (would have thrown and wiped this player's perks).` and `Ectype: saved perk <name> (<guid>) held at level <n> but has <m> - lowered to <m>, <n-m> point(s) return as unspent.`

A perk has fewer levels than a save or a peer holds, because a mod update removed levels. The
framework applies the top level instead of throwing. The second line is the owner's profile
being corrected: the extra points return as unspent.

### `Ectype: scrubbed saved perk <guid> from a loadout (its tree is suppressed; the point returns as unspent).`

A saved loadout held a perk from a stock tree that a class mod now suppresses. The point is
refunded.

### Things that look like they should work

- **Renaming a `classId` or a perk `key` after release.** Both seed GUIDs. A rename orphans
  every saved selection exactly as deleting the perk would. Pin an explicit `guid` on a perk to
  survive a key rename.
- **A perk row above 7, or a `col` above 2 on a three-cell row.** Validation only rejects
  `row` below 1. The game throws `ArgumentOutOfRangeException` when the terminal builds, and the
  terminal breaks. Rows 2, 5 and 7 are single cells and need `"wide": true`.
- **A level list written as increments.** Each level's list is the full effect at that level.
  A ladder of +10%, +20%, +30% is written 0.10, 0.20, 0.30.
- **An effect on a vanilla class-ability stat.** `ClassAbilityDuration` and
  `ClassAbilityCooldown` reach every class ability on the character. Register the ability's own
  timing under class-declared stats with `ClassStats.RegisterAbilityTiming` and name those.
- **Naming both ids of a pair on one level.** The hand-attractor stats and their vanilla mirror
  ids compound on one float when a single source names both. Name one.
- **A custom stat with no code reading it.** It registers and shows in tooltips and changes
  nothing.

## Presentation

### `Ectype: tree VE injection for '<name>' failed: <exception>` and `Ectype: tree element for '<name>' missing - its PerkTreeDef was filtered out of this build so the terminal survives without it.`

The framework could not build that class's tree element in a terminal. It removes the class's
tree definition from that terminal's build so the terminal opens without it. Other classes are
unaffected. The exception names the cause; if the next two entries appear before it, they are
the cause.

### `Ectype: no 'Scavenger' PerkTreeVE in this document - cannot place tree '<t>'.`, `Ectype: could not resolve the owning UIDocument (or its visualTreeAsset) for this root - cannot instantiate a pristine tree.`, `Ectype: no 'Scavenger' PerkTreeVE in a fresh instantiation of <asset>.`

The framework builds each class tree from the game's authored Scavenger tree element. The
terminal document no longer has one, or its asset could not be resolved. A game update changed
the terminal layout.

### `Ectype: palette install failed: <exception>`

The per-class colour stylesheets could not be attached to a terminal. Trees render untinted.

### `Ectype geometry [<document>]: document is attached, shown and in a sized panel, but its tree row still has no width after 20s - layout failed.`

The tree row of a terminal never received a width, so nothing shows. A dump of the row's
layout follows in the log. Another mod's stylesheet or a game update is the usual cause. Set
`[Presentation] EnableScroll = false`, or add a substring of the document's name to
`[Presentation] ScrollExcludeDocuments` (the dump prints each document's names) to keep the
authored layout, and report the dump.

### `Ectype: tree scroller install failed in [<document>]: <exception>` and `Ectype: no PerkTreeVE in this document - nothing to wrap.`

With more than four trees the row becomes a horizontal scroller. The wrap failed in that
document. The extra trees overflow off the layout. Use `ScrollExcludeDocuments` as above while
reporting it.

### More than four trees and the extra trees are off screen

`[Presentation] EnableScroll` is `false`, or the document is listed in
`ScrollExcludeDocuments`.

### `Ectype: HUD document has no ClassAbilityContainer/Scavenger element - HUD slot skipped (game update?).`

The HUD layout no longer has the element the class-ability slot is built from. Classes work
without a HUD slot. The same line with `HUD bar buttons are skipped this session` applies to
the ability bar.

### `Ectype: HUD ability-slot wiring failed (reported once; modded classes run without a HUD slot): <exception>`

The slot for a framework class's ability could not be built. The ability still works through
its key.

### `Ectype: HUD ability element has no '<name>' child - that layer keeps the donor's glyph (game update renamed it?).`

A layer of the slot element is missing. The class's icon does not show on that layer.

### `Ectype: HUD slot '<name>' for '<classId>': glyph MISSING (donor or placeholder shows), siblings [...]`

Logged once per class under `VerboseLogging`. `glyph MISSING` means the hero perk's `icon` key
matched no embedded PNG. The slot shows the donor's art or the placeholder.

### `Ectype: HUD role icon install failed: <exception>` and `Ectype: lobby role icon install failed: <exception>`

The class's role icon could not be drawn in the crew HUD or the lobby list. The class works.

### `Ectype: no loaded sprite named '<name>' - that pip layer is left blank.`

A stock sprite the scroller's page indicator borrows is not loaded. The indicator draws
without that layer.

### HUD ability bar

| Line | Cause |
| --- | --- |
| `Ectype: HUD bar refused a button with no id.` | `HudButtonSpec.Id` is empty. |
| `Ectype: HUD bar refused '<id>': that id is already registered.` | Two buttons share an id. Prefix ids with your plugin GUID. |
| `Ectype: HUD bar refused '<id>': the bar holds 4 buttons and they are taken by <ids>.` | The bar holds at most four buttons across every mod. `Register` returns `null`. |
| `Ectype: HUD bar button '<id>' threw and is hidden for the rest of this session: <exception>` | One of the spec's callbacks (`Key`, `Fill`, `Visible`) threw. That button is hidden; the rest of the bar runs. |
| `Ectype: HUD bar glyph '<name>' is not readable, so its corners are not trimmed to the frame; content there may show outside it.` | The glyph texture was created without read access. Make it readable, or keep the glyph clear of the corners. |

## Abilities

An ability is a `ClassAbility` subclass returned by a factory registered with
`ClassRegistry.RegisterAbilityFactory(classId, factory)` at the plugin's `Awake`. The framework
builds one per character spawn, installs it into the character's Opsive locomotion, and points
it at the class's hero, row-5 and row-7 banner perks.

### `Ectype: ability factory registered for '<classId>'.`

The factory call arrived. If the line is absent, the call never ran: check plugin load order
and the `[BepInDependency]` attribute. A factory registered for a `classId` with no manifest
does nothing.

### `Ectype: ability factory for '<classId>' threw - the class stays data-only this session: <exception>`

The factory threw on a character spawn. The class has no ability this session and its perks
still apply. Fix the exception.

### `Ectype: ability factory for '<classId>' returned null or a non-ClassAbility - the class stays data-only this session.`

The factory must return a `ClassAbility` subclass. The simplest base is
`EctypeClassAbility`, which wraps the effect hooks.

### `Ectype: ability field wiring failed for '<classId>' (member rename? see warnings above) - ability not installed.`

One of the identity fields on the ability (`AbilityPerk`, `CapstonePerk`,
`SuperiorAbilityPerk`, `abilityUsable`, `tags`) could not be set. The warning above the error
reads `field '<name>' not found on <Type> (game update?)` and names the missing field. A game
update renamed it.

### `Ectype: installing '<classId>' ability into the locomotion failed: <exception>`, `Ectype: ability install sweep failed: <exception>`

Installing the ability into the character failed. That character has no ability from the class
until it respawns. Other characters are unaffected.

### `Ectype: could not read m_CharacterLocomotion off a ClassAbility - game/Opsive update? Modded abilities will not install.`

The field the framework reads the locomotion from is gone. No framework ability installs.

### `Ectype: <AbilityType>.OnApplyEffect threw (reported once): <exception>` and `... OnRemoveEffect threw ...`

An override on an `EctypeClassAbility` subclass threw. The base class catches the exception so
Opsive's update loop keeps running, and logs it once per ability type. Fix the override.

### `Ectype: could not repoint <VanillaAbilityType> onto '<classId>' (field rename? see warnings above) - that class has no active ability.`

A class suppresses a stock tree and takes over the stock ability that tree owned. The repoint
failed because a field was renamed. The class has no ability.

### `Ectype: ClassAbility.VerifyIfIsSelectedAbility not found - game update? Adopted abilities will not select until the next loadout change.`

An adopted stock ability selects only after the next loadout change.

### `Ectype: deciding the capstone payload for <AbilityType> failed - the vanilla payload stays, and it may stack with the class's authored effects: <exception>`

A class that adopted a stock ability and authored its own capstone effects may receive both.

### Things that look like they should work

- **The ability key does nothing for the first seconds after selecting the perk.** Selecting
  the hero perk, and every respawn, starts the full cooldown, as for any class ability.
- **An ability added at runtime works in solo and breaks other players.** Opsive replicates
  ability start and stop by position in the character's ability array. A client whose array
  differs resolves the number against the wrong ability or past the end of the array, and the
  exception lands inside the engine's own tick on every other client, once per start and once
  per stop. Register the ability with `RuntimeAbilities.Register(ability, key)` after it is in
  the array, with a literal key namespaced by your mod. The framework then replicates it by key
  on its own channel. A framework class ability is registered automatically as
  `ectype:<classId>`.
- **A `RuntimeAbilities` key computed from runtime state.** The key must be the same on every
  client for the same ability. A key built from an index, an instance id or anything that
  differs between clients never matches.

## Multiplayer

### A joiner is refused at join

Which screen says what the problem is:

| What the joiner sees | Check that refused | Cause |
| --- | --- | --- |
| `Disconnected: Incompatable mod list` (VoidManager's spelling) | VoidManager, on the joiner's client | The two mod lists differ in a mod declared `All`: a different version, or a mod only one side has. |
| `Host is missing the required mod` | VoidManager | The joiner has an `All` mod the host lacks. |
| `Client is missing the required mod` | VoidManager | The host has an `All` mod the joiner lacks. |
| A screen that lists individual assets | The game's asset check | One side registered required content (a class's tree or perks, for example) that the other did not. |
| Removed from the room after arriving, with no message from the framework | VoidManager, on the host | Same version, different DLL bytes. Only the host compares hashes. |

Fix every case the same way: give everyone the same files. Raise the version of any mod whose
bytes changed. After a framework update, rebuild and reship every mod that depends on it.

### Everyone has the same version and one player is still refused

The two DLLs differ. A framework update changes the bytes of every dependent built against it,
because each records the framework assembly version it was compiled against, so a locally
rebuilt dependent differs from a downloaded one at the same version number. Reship from one
set of files.

### A player lacks a class mod and the room runs

It does not. The asset check refuses the join in both directions. See `multiplayer.md`.

### `Ectype: selection gate for <guid> FAILS (<why>): <names>`

The host's gate found members who lack the mod or run a different build. `<names>` reads
`<player> (missing)`, `<player> (version <v>)` or `<player> (different build of <v>)`. `<why>`
is the event that triggered the evaluation: `registered <guid>`, `mod list received`,
`player left`, `host switched`, `player entered`, `joined room`, `left room` or `requested`.
The warning is logged when the result changes, once per change. The dependent decides what to
do with it. Give the named players the host's files.

### `Ectype: selection gate for <guid> unavailable (<why>): <reason>`

The gate could not evaluate. `<reason>` is one of:

| Reason | Cause |
| --- | --- |
| `VoidManager's own mod list is not readable` | The framework reads VoidManager's private list by reflection and the field is gone. A VoidManager update. Also logged once as `Ectype: MPModCheckManager.MyModList not found (VoidManager update?) - every selection gate reports Unavailable.` |
| `the host's mod list does not carry <guid>` | The GUID passed to `Register` or `Evaluate` is not a loaded plugin's GUID. Check the spelling against the plugin's `[BepInPlugin]` attribute, and that the plugin declares a `VoidPlugin`. |
| `evaluation threw: <ExceptionType>` | The gate threw. The full exception is logged as `Ectype: selection gate evaluation for <guid> threw: <exception>`. |

### `Ectype: selection gate for <guid> is <verdict> (<why>): <names>`

Logged only with `VerboseLogging`. A routine change of verdict. `<names>` lists members that
are not `Match`: `(mod list not received)` and `(version only, no hash)` are the non-failing
ones.

### The gate stays `Unverified`

Every member's version matches and the host holds no hash for at least one. The host holds no
hash for a member when the room changed host and that member's resent list has not arrived. Look
for `Ectype: cannot re-send the mod list to the new host (<what> not found; VoidManager update?) - it will see this client as Unverified.`
on the members' machines: it means the resend could not run, and those members stay unverified
until they rejoin. `Ectype: re-sending the mod list to the new host threw: <exception>` is the
same failure with an exception.

### The gate returns `NotHost` or `Pass` and the dependent does nothing

`NotHost` is the answer on every client except the master. `Pass` is also the answer offline,
solo and outside a room. A dependent that applies policy on the host only should read the
gate on the host only.

### `Ectype: a selection gate callback for <guid> threw: <exception>` and `Ectype: selection gate re-evaluation (<why>) threw: <exception>`

The dependent's callback threw, or the evaluation loop did. The framework catches both so the
other callbacks still run. Fix the callback.

### Host suppression

| Line | Meaning |
| --- | --- |
| `Ectype: published host suppression to the room: '<classIds>'.` / `(cleared).` | The host wrote its per-class toggles to the room. This happens only while the host is in the hub. |
| `Ectype: host suppression changed +[<ids>] -[<ids>].` | This client applied a new suppressed set. It then reapplies every player's perks and rebuilds the gene-tree screens. |
| `Ectype: rebuilt <n> gene-tree screen(s) for the suppression change.` | The open terminals were rebuilt. |
| `Ectype: gene-tree screen on '<name>' has no trees or no authored template - left as built, so it shows the old suppression until a respawn.` | One terminal could not be rebuilt. It updates after a respawn. |
| `Ectype: host suppression armed - session flagged via VoidManager.PluginHandler.EscalateSession.` | The first suppression marked the session as modded. |
| `Ectype: PluginHandler.EscalateSession not found; falling back to ModdingUtils.RegisterSessionMod.` and the matching `threw` form | VoidManager's internal method moved. The public fallback sets the same flag and does not notify other VoidManager clients. |
| `Ectype: could not flag the session for host suppression: <exception>` | Both routes failed. The room is not marked by this route. |
| `Ectype: publishing host suppression failed: <exception>`, `Ectype: reading host suppression room property failed: <exception>`, `Ectype: suppression rederive after host-suppression change failed: <exception>`, `Ectype: re-applying a player's perks after a host suppression change failed: <exception>`, `Ectype: gene-tree rebuild after host-suppression change failed: <exception>` | A step of applying a suppression change threw. The remaining steps still ran. |

Things that look like they should work and do not:

- **A suppressed class still has to be installed by everyone.** Suppression hides a class and
  leaves its assets registered, so the asset check still compares them.
- **A toggle changed during a run does nothing until the host is in the hub.** The host
  publishes its toggles only from the hub.
- **Suppression does not delete saves.** The perks stay in each loadout and read as absent.
  Lifting the suppression brings the build back.

### Mutator guards

The framework removes mutators the local client cannot resolve, so a client missing a mutator's
mod does not throw when the host activates it.

| Line | Meaning |
| --- | --- |
| `Ectype: mutator <guid> is active this session but not registered on this client (its mod is not installed here) - skipped.` | The host started a run with a mutator this client lacks. The run starts without it on this client. Install the mutator's mod to take part in it. |
| `Ectype: mutator <guid> is listed (saved or selected session) but not registered on this client - hidden from the mutator list until its mod is enabled.` | A saved run or a selected session names a mutator whose mod is missing or disabled. The saved list is left as it was; enabling the mod brings the entry back. |
| `Ectype: mutator-safety filter failed, starting mutators unfiltered this session: <exception>` and `Ectype: mutator-list UI filter failed, list unfiltered: <exception>` | The guard threw. The game's own unguarded code runs and a missing mutator throws a `NullReferenceException`. |

### Ability replication

| Line | Cause |
| --- | --- |
| `Ectype: ignored ability-sync for view <id> from a player who does not own it.` | A message tried to drive a character it did not own. Logged once per sender and view. Another client is running something that sends ability messages for characters it does not own. |
| `Ectype: CustomPunCharacter.<name> not found - <consequence> (game update?)` | A game method the replication patches is gone. The consequence names what stops working (late joiners not learning running abilities, for one). |
| `Ectype: ability-sync send failed: <exception>`, `Ectype: handling ability-sync message failed: <exception>`, `Ectype: late-join ability-sync failed: <exception>`, `Ectype: resolving ability-sync recipients failed: <exception>` | A step of the keyed channel threw. A registered ability never falls back to the index RPC, so a failed send is a dropped state update. |

A registered ability reaches only peers whose mod list carries the framework. A peer without it
sees no message it cannot act on and ignores keys it has no ability for.

## Utility APIs

These lines come from the APIs a non-class mod calls.

### Enemy and character statuses

| Line | Cause and fix |
| --- | --- |
| `Ectype: status '<key>' is not registered on this client. Register the template at Awake.` | `Apply` was called with a key nobody registered here. Call `EnemyStatuses.Register` at `Awake` on every client from a literal key. Logged once per key. |
| `Ectype: peer applied unknown enemy status '<key>'; ignoring (mod not installed here?).` | Another player applied a status this client has no template for. The status does not exist on this client. Install the mod, or accept that the peer's simulation differs. |
| `Ectype: status '<key>' on a null/viewless target ignored.` | The target is null or has no `PhotonView`. |
| `Ectype: Apply('<key>') with duration <s>s ignored.` | The duration is zero, negative or not finite. |
| `Ectype: Apply('<key>') skipped; <target> is invulnerable.` | `SkipsInvulnerableTargets` is set and the target's `Invulnerability` stat is above zero. |
| `Ectype: status '<key>' matched no registered stat on <Type>; it has no effect there.` | None of the status's stat entries names a stat the target registers. The game ignores an unmatched stat without logging. Use `OnApplied` and `OnRemoved` for anything without a registered stat. |
| `Ectype: status-<marker or vfx> cap (<n>) reached; further statuses run without <markers or visuals> until one frees up ([EnemyStatuses] <key>).` | `[EnemyStatuses] MarkerCap` or `VfxCap` is full. The status runs undecorated. |
| `Ectype: status '<key>' vfx donor '<name>' not found among loaded prefabs; no visual attached.` | `VfxDonorName` matches no loaded prefab. Names come from a prefab dump of the running game. |
| `Ectype: framework is inert this session; enemy statuses are disabled.` and the character-status form | The framework's patches did not apply. See `FATAL`. |
| `Ectype: status '<key>' re-registered; replacing the previous template.` | Two registrations of one key. The later one wins. |
| `Ectype: character status '<key>' asks for replication, which is not implemented. It runs on the applying client only; callbacks and visuals are not shared.` | `Replicated = true` on a character status. A character status is local to the applying client. |
| `Ectype: status '<key>' presentation failed`, `tick failed`, `stat removal failed` (each with `(suppressing further reports)` where shown) | A callback or presentation step threw. Logged once per status. |

Enemy statuses replicate; late joiners do not receive statuses already running.

### Loot tables

| Line | Cause and fix |
| --- | --- |
| `Ectype: loot: a registration needs an item GUID and an anchor; ignored (<description>).` | `LootDrop.Item` or `IsAnchor` is missing. |
| `Ectype: loot: <owner> tried to register <guid>, which <owner> already registered; the first registration stands.` | Two registrations of one item GUID. |
| `Ectype: loot: anchor tag '<name>' did not resolve (<message>); it matches nothing.` | `AnyCarryableTagged` names a tag the game lacks. |
| `Ectype: loot: LootTable.ApplyLimiters not found (game update?); registered items will not drop.` | The patched method is gone. Registration succeeds and nothing drops. |
| `Ectype: loot: ApplyLimiters ran on '<name>', which is not a per-roll copy; nothing added, so no shipped table is ever written.` | The framework refuses to write a shipped table. Informational. |
| `Ectype: loot: <item> left out of <table>, which offers no <rarity> entry. Logged once per table.` | `RequireRarityInTable` is set and the table has no entry at the item's rarity. |
| `Ectype: loot: gate for <item> is now closed (roll on <table>).` | The `Gate` function returned false. Logged when the answer changes. A gate that throws is treated as closed (`gate for <item> threw; treated as closed`). |
| `Ectype: loot: anchor warm-up failed and is off for this session; rolls resolve their items as they meet them: <exception>` | The preload failed. Rolls still work and the first roll that meets an unloaded item stalls on its load. |

An item drops only into a table that holds an authored entry its anchor accepts. A registered
item that never drops usually has an anchor no table matches.

### Timed stat contributions, ship surge, arrivals

| Line | Cause |
| --- | --- |
| `Ectype: timed stat '<key>' not started: it needs a key, a target, an amount function and a finite positive duration.` | An argument is missing or the duration is not positive and finite. |
| `Ectype: timed stat '<key>' names stat <name>, which <holder>'s collection tree does not register; it has no effect there.` | The stat is not in the target collection's tree. |
| `Ectype: ship speed surge not started (ship <name>, magnitude <m>, <d> s): it needs a ship, a magnitude above -1 other than 0, and a positive duration.` | An argument is out of range. A surge also refuses to start outside normal flight. |
| `Ectype: reading <State>.enterTimestamp failed; arrivals are stamped with this machine's clock: <message>` | The replicated exit timestamp could not be read. Arrival times on different machines can differ. |
| `Ectype: ShipArrivals.<event> subscriber <Type>.<method> threw: <exception>` | A subscriber threw. Each subscriber runs in its own `try` block, so the others still ran. |

### Field-backed stats

| Line | Meaning |
| --- | --- |
| `Ectype: wiring <label> as a stat failed - the field keeps its authored number: <exception>` | The stat could not be attached. Perks that name it do nothing. |
| `Ectype: ChannelGrapplingHook.<name> not found (game update?) - that grapple stat is registered nowhere and perks written against it do nothing.` | A field the framework wraps no longer exists. |
| `Ectype: BuildProcess.StateTimer is not reachable - ModuleInstallSpeed is inert: <exception>` | The module install-speed stat has no effect. |
