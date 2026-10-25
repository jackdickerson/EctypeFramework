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
| | `no role manifests found` | No installed mod carries a manifest. See "No role appears". |
| | `<n>/<m> roles registered` | `n` of `m` discovered roles finished registering. `(<k> failed)` is appended when `k` gave up. |
| `Stats:` | `<n> vanilla aliases, framework-owned <status>, <k> mod-declared[, <e> error(s)]` | The stat names a manifest can use. `not registered` means the stat table has not loaded; `<n> registered` is the normal state. `<k> mod-declared` counts stats from mods' stats files (`not discovered` before the stat table loads); each error has its own `mod stats` line in the log. |
| `Session stats:` | `no session`, `session <id>, host`, `session <id>, client`, or `inert: session data unavailable` | The run-wide stat collection. `inert` means the session data hooks are missing, and every session stat reads its declared default. |
| `Network :` | `event 181, contract <n>, marker <guid>` | The framework's Photon event code, the network contract this build speaks, and the contract marker asset once registered (`marker not registered yet` before the asset tables load). |
| `Suppress:` | `not resolved yet`, `nothing to suppress`, `no trees suppressed`, `<n> tree(s) suppressed: <names>`, or `<k> suppressed role(s)` | Which stock gene trees role mods have removed through `suppress`, and how many framework roles other role mods have removed through `suppressRoles`; both parts appear joined by `+` when both apply. `(<n> ignored)` counts manifest entries that named nothing usable; each has its own warning. |
| One line per role | `registered (stage limit 9); tree def published as EPerkTree <n>` | The role is registered and its tree is published. |
| | `waiting for asset containers` | Same as the pipeline line, for this role. |
| | `attempt <n> failed at [<stage>]: <message>` | Registration threw. See "Registration attempt failed". |

The role line also shows the tree number and the assembly that supplied the manifest.

## Load

### `Ectype Framework v<version> loaded (contract v<n>).`

The framework started and applied every patch. `n` is the manifest contract version this build
accepts. No role is registered yet; look for `Ectype: discovered` next.

### `Ectype: settings menu unavailable: VoidManager not loaded.`

VoidManager is not installed, or BepInEx did not load it. The framework runs in full; only the
F5 page is missing, since F5 is VoidManager's menu. Install VoidManager to get the page back.

With VoidManager installed, these lines say a hook into it missed after a VoidManager update:
`VoidManager.MPModChecks.MPModCheckManager.ModChecksClientside not found` and
`VoidManager join-check guard failed` (joining a host without VoidManager fails inside
VoidManager's own check), `VoidManager.PluginHandler.DiscoverPlugins not found` and
`adding the F5 page to VoidManager failed` (the F5 page is missing). The game's asset check
still compares the network contract in every case.

VoidManager lists the framework among its non-VoidManager mods, as `Unmanaged`: the framework
declares no VoidManager type, so VoidManager never refuses a join on its account.

### `Ectype: the host runs no VoidManager (modded room with no mod list); VoidManager's join check is skipped and the game's asset check decides.`

Logged once, on a client with VoidManager joining a host without it. VoidManager's own check
needs the host's mod list and would fail without one; the framework passes it for such a room.

### `Ectype: disabled via config ([General] Enabled = false).`

The framework did nothing: no patches, no registration, no driver. Set `[General] Enabled` to
`true` in `jack.voidcrew.ectype.cfg` and restart. Mods that depend on the framework still load
and still declare their multiplayer type, but nothing they registered with the framework runs.
A disabled framework registers no contract marker, so the game refuses a join between this
player and anyone running the framework enabled, in either direction.

### `Ectype: FATAL - patching failed, framework is inert this session and no roles register: <exception>`

A Harmony patch could not be applied. Harmony does not undo patches applied before the failure,
so the framework treats a partial set as unusable: it starts no driver and every registration
entry point refuses to run. The F5 pipeline line reads `inert: patching did not complete`.

The usual cause is a game update that renamed or reshaped a patched method. The exception names
it. Install a framework build made for the installed game version. If one does not exist, report
the exception text.

### `Ectype: enemy ability gates failed: <exception>` and `Ectype: loot table patch failed: <exception>`

The enemy-ability suspension patches or the loot-table patch could not be resolved. Each system
is patched on its own, so a renamed game member disables only that system. Role registration
and the other utilities are unaffected. Cause and fix are the same as for `FATAL`.

### A dependent calls the framework and gets a `NullReferenceException`

The framework assigns its logger in its own `Awake`. A dependent that calls a framework API
(`RoleRegistry.RegisterAbilityFactory`, for one) before that `Awake` has run fails inside the
call. Declare `[BepInDependency("jack.voidcrew.ectype")]` on the dependent's plugin class.
BepInEx then runs the framework's `Awake` first. Without the attribute, load order is not
defined.

## Manifest validation

A manifest is an embedded resource whose name ends in `ectype-role.json`. The framework scans
every loaded plugin assembly once, at the first registration pass, and logs
`Ectype: discovered <n> role(s): <summary>.` when the scan ends.

### `Ectype: manifest '<resource>' in <assembly> rejected: <problem>`

The manifest parsed as JSON and failed a check. The role is skipped and nothing else is
affected. `<problem>` is one of:

| Problem | Cause and fix |
| --- | --- |
| `empty document` | The resource is empty or `null`. |
| `needs Ectype Framework <x> or newer (this is <y>)` | `minFrameworkVersion` is newer than the installed framework. Update the framework, or lower the field if the role uses nothing newer. |
| `minFrameworkVersion '<x>' is not a version (expected "major.minor.patch")` | Write the field as three dot-separated numbers. |
| `contractVersion <n> (this framework speaks 2)` | The manifest declares a contract this framework does not read. Set `contractVersion` to the value the framework prints in its load line. |
| `no roleId`, `no roleName` | A required field is missing or empty. |
| `no hero perk` | `hero` is missing. Every role needs a row-0 hero perk. |
| `null perk entry` | The `perks` array holds a `null`. |
| `a perk has no key` | Every perk needs a `key`. |
| `duplicate perk key '<k>'` | Keys are compared without regard to case and must be unique within a role. The hero's key counts (it defaults to `hero`). |
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

### `Ectype: role '<name>' perk '<key>' skipped: unrecognised target '<x>' (this framework knows character, seat, seatShip, characterShip).`

A non-hero perk names a `target` the framework does not know. Only that perk is dropped. Fix the
spelling; the comparison ignores case.

### No role appears and the log says `Ectype: discovered 0 role(s).`

The framework found no manifest. The discovery rule is that an embedded resource name ends with
`ectype-role.json`, compared without regard to case. Check that the file is marked as an
embedded resource in the project and that its resource name (the assembly's root namespace and
folder path, joined with dots, unless the project sets a logical name) ends with that suffix.
A manifest named `ectype-class.json` was written for contract 1 and is not discovered: rename
the resource, its fields and its `contractVersion` as [manifest.md](manifest.md) gives them.
Check also that the plugin loaded: a plugin that failed to load never has its assembly scanned.
The framework scans assemblies of loaded BepInEx plugins only.

### `Ectype: duplicate roleId '<id>' in <assembly> (already provided by <assembly>) - the duplicate is skipped.`

Two installed mods declare the same `roleId`. The first one the scan met wins. Rename the
`roleId` in one of them. The id seeds every GUID of the role, so renaming it after release
orphans saved selections; pick a unique id before releasing.

### `Ectype: discovery requested after registration closed (first terminal build) - refused.`

The first gene-tree terminal was built before discovery ran. The ordinal each role receives
depends on the full set of roles, so a role that appeared later would get a different
ordinal on different clients. Discovery is refused instead. Restart the game. If it recurs on
every launch, the framework's registration is not reaching the point where it runs; check the
log for an earlier `FATAL` or `Pipeline` line.

### Role colour warnings

| Line | Meaning |
| --- | --- |
| `Ectype: role '<name>' declares no color - its tree will render untinted (white).` | `color` is missing. |
| `Ectype: role '<name>' color '<c>' is not parseable (expected "#RRGGBB") - the tree will render untinted (white).` | Use a six-digit hex string with a leading `#`. |
| `Ectype: role '<name>' color <c> is too close to white (R+G+B = <n> >= 700) - it will be hard to tell from an untinted tree.` | The red, green and blue values sum to 700 or more of 765. Pick a darker colour. |
| `Ectype: roles '<a>' (<c>) and '<b>' (<c>) have nearly identical accent colors (distance <n> < 90) - the trees will be hard to tell apart.` | The red, green and blue differences between two installed roles sum to less than 90 of 765. Change one. The stock roles use red, blue, green and purple. |

### Suppression warnings

All are logged when the framework resolves what a role mod suppresses. None fails a role.

| Line | Cause |
| --- | --- |
| `Ectype: suppress entry <guid> (from '<roleId>') names a framework-registered role asset - name its roleId in suppressRoles instead. Entry ignored.` | `suppress` takes stock tree GUIDs. Move the role to `suppressRoles`. |
| `Ectype: suppress entry <guid> (from '<roleId>') is a tree whose def or asset did not resolve - entry ignored.` | The GUID names a tree whose data did not load. Check the GUID. |
| `Ectype: suppress entry <guid> (from '<roleId>') names a single perk; only whole trees can be suppressed. Entry ignored.` | A perk GUID is not a tree GUID. |
| `Ectype: suppress entry <guid> (from '<roleId>') resolves to nothing in this game version - entry ignored, nothing suppressed.` | The GUID does not exist in the installed game. |
| `Ectype: role '<id>' lists itself in suppressRoles - entry ignored.` | Remove the entry. |
| `Ectype: role '<id>' suppresses role '<other>', which is not installed - entry ignored.` | Harmless when the other role is optional. |
| `Ectype: suppressRoles cycle through '<id>' - treated as active.` | Role A lists B and B lists A. The cycle is broken in `roleId` order. |

## Registration

Registration runs in stages for each role: `guids`, `custom stats`, `perk assets`, `runtime
assets`, `perk defs`, `tree asset`, `tree def`. It retries every 2 seconds until every stage
passes, because the game's tables load some time after the plugin's `Awake`.

### `Ectype: role '<name>' registered. Tree <guid> as EPerkTree <n>, <m> perk(s). <status>`

Success. `n` is the tree's ordinal, assigned by sorting `roleId` values, so every client with
the same set of role mods gets the same ordinal.

### `Ectype VERIFY '<name>': ok. <n> perk(s) resolve; tree asset built; tree def published.`

The follow-up check after registration. Two failure forms exist:

- `Ectype VERIFY '<name>': perk '<key>' does not resolve by GUID.` The perk's asset did not
  enter the game's registry. Look for an earlier `registration` warning for the role.
- `Ectype VERIFY '<name>': perk '<key>' def.Asset is null - ref not IsRuntime, or the asset was collected.` The registered asset was released. Report it with the log.

`tree def not published` in the success line means the tree def stage was skipped: either
`[General] RegisterStage` is below 6, `[General] PublishTreeDefs` is `false`, or the tree
controller patch is missing (next entry). Reset those keys to their defaults, which are `9` and
`true`.

### `Ectype: '<name>' registration attempt <n> failed at [<stage>]: <message>`

A stage threw. The framework logs the first failure and every tenth after it, and tries again
after 2 seconds. After 200 attempts it gives up with
`Ectype: giving up on '<name>'. Last error: <exception>` and the role is absent for the
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
| `unknown stat '<s>' (not a StatType name, not a framework-owned stat, not a custom stat of this role, not an int id)` | The stat name is misspelled or does not exist. Names are compared without regard to case. The list is in `docs/stat-names.md`. A stat declared by another mod cannot be named. |
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

### `Ectype: '<name>' perk '<key>' unlocks item <guid>, which another role's perk ('<name>' perk '<key>') already claimed this session. Skipped: this perk grants no fabricator unlock for it.`

Two installed roles list the same item in their `unlocks`. The first to register holds the
unlock definition, and the second perk registers normally but grants nothing for that item.
Remove the entry from one role, or give each role its own item.

### `Ectype: carryable '<name>' (<guid>) is registered with no crafting data; it breaks the end-of-run alloy tally, ...`

A mod registered a carryable at runtime without a crafting definition under the same GUID. The
game's recycled-alloy count reads the crafting rules of every carryable on the player ship with
no null check, so it throws on this one. It runs on the host at session start and at the end of
every run, while rewards are computed, in a one-ship vanilla-rules room too. The line names the
asset; the mod that registers it needs to register crafting data for it. The framework checks
each runtime asset once, after its own roles have settled, and logs nothing in an install with
no such carryable.

`Ectype: crafting-data audit failed and is off for this session: <exception>` means the check
itself threw; a carryable without crafting data registered from then on is not reported.

### `Ectype: perk '<name>' (target <t>) has an effect on <stat> that matches no stat in <where> collection tree - it stays registered and changes nothing there.`

A perk's effect names a stat that the perk's `target` does not hold. A stat's category never
limits a target, so the effect registers and does nothing. The framework checks after the perk
reapply, and counts a miss only when a reapply at least 10 seconds later still misses, because
modules and abilities join the stat tree after spawn. Change the `target`, or change the stat.

### `Ectype: the PerkTreeController ctor prefix is not applied - tree defs are not published this session. Roles register but no tree renders.`

The patch that injects role trees into the terminal did not apply. The framework withholds
every tree def rather than let the terminal throw. Cause is the same as for `FATAL`.

### `Ectype: dropped perk GUID <guid> before ReApplyPerks (suppressed)` or `(unresolvable - would have thrown and wiped this player's perks)`

A player's saved or replicated perk list holds a GUID that no longer resolves. This happens
when a role mod is uninstalled, when a role's `roleId` or a perk's `key` changed, or when
another player has a role this client lacks. The game's perk reapply would have thrown and
left the player with no perks, so the framework removes the entry first. The save keeps the
entry; reinstalling the mod restores the perk. `(suppressed)` is the same drop for a perk of a
suppressed role.

### `Ectype: perk GUID <guid> held at level <n> but has <m> level(s) - applying level <m> (would have thrown and wiped this player's perks).` and `Ectype: saved perk <name> (<guid>) held at level <n> but has <m> - lowered to <m>, <n-m> point(s) return as unspent.`

A perk has fewer levels than a save or a peer holds, because a mod update removed levels. The
framework applies the top level instead of throwing. The second line is the owner's profile
being corrected: the extra points return as unspent.

### `Ectype: scrubbed saved perk <guid> from a loadout (its tree is suppressed; the point returns as unspent).`

A saved loadout held a perk from a stock tree that a role mod now suppresses. The point is
refunded.

### Things that look like they should work

- **Renaming a `roleId` or a perk `key` after release.** Both seed GUIDs. A rename orphans
  every saved selection exactly as deleting the perk would. Pin an explicit `guid` on a perk to
  survive a key rename.
- **A perk row above 7, or a `col` above 2 on a three-cell row.** Validation only rejects
  `row` below 1. The game throws `ArgumentOutOfRangeException` when the terminal builds, and the
  terminal breaks. Rows 2, 5 and 7 are single cells and need `"wide": true`.
- **A level list written as increments.** Each level's list is the full effect at that level.
  A ladder of +10%, +20%, +30% is written 0.10, 0.20, 0.30.
- **An effect on a vanilla role-ability stat.** `ClassAbilityDuration` and
  `ClassAbilityCooldown` reach every role ability on the character. Register the ability's own
  timing under role-declared stats with `RoleStats.RegisterAbilityTiming` and name those.
- **Naming both ids of a pair on one level.** The hand-attractor stats and their vanilla mirror
  ids compound on one float when a single source names both. Name one.
- **A custom stat with no code reading it.** It registers and shows in tooltips and changes
  nothing.

## Presentation

### `Ectype: tree VE injection for '<name>' failed: <exception>` and `Ectype: tree element for '<name>' missing - its PerkTreeDef was filtered out of this build so the terminal survives without it.`

The framework could not build that role's tree element in a terminal. It removes the role's
tree definition from that terminal's build so the terminal opens without it. Other roles are
unaffected. The exception names the cause; if the next two entries appear before it, they are
the cause.

### `Ectype: no 'Scavenger' PerkTreeVE in this document - cannot place tree '<t>'.`, `Ectype: could not resolve the owning UIDocument (or its visualTreeAsset) for this root - cannot instantiate a pristine tree.`, `Ectype: no 'Scavenger' PerkTreeVE in a fresh instantiation of <asset>.`

The framework builds each role tree from the game's authored Scavenger tree element. The
terminal document no longer has one, or its asset could not be resolved. A game update changed
the terminal layout.

### `Ectype: palette install failed: <exception>`

The per-role colour stylesheets could not be attached to a terminal. Trees render untinted.

### `Ectype geometry [<document>]: document is attached, shown and in a sized panel, but its tree row still has no width after 20s - layout failed.`

The tree row of a terminal never received a width, so nothing shows. The line
carries a dump of the row's layout, and it appears only with `[Debug] VerboseLogging = true`,
the only setting that runs the watcher. Another mod's stylesheet or a game update is the usual cause. Set
`[Presentation] EnableScroll = false`, or add a substring of the document's name to
`[Presentation] ScrollExcludeDocuments` (the dump prints each document's names) to keep the
authored layout, and report the dump.

### `Ectype: vanilla role dump written to <path> (<n> trees).` and `Ectype: vanilla dump attempt <n> failed: <message>`

Both come from a dev tools build with `[Dev] WriteVanillaDump = true`
([CONTRIBUTING.md](../CONTRIBUTING.md#dev-tools-build)). The first confirms
`BepInEx/EctypeDump/vanilla-roles.json` was written. The second is a failed attempt, retried
on the next registration pass and logged on the first and the twentieth failure, after which
the dump stops trying for that launch. If `vanilla-roles.json` does not exist, the build is a
release build or the switch is off: set it in `BepInEx/config/jack.voidcrew.ectype.cfg`,
restart the game and reach the main menu.

### `Ectype: tree scroller install failed in [<document>]: <exception>` and `Ectype: no PerkTreeVE in this document - nothing to wrap.`

With more than four trees the row becomes a horizontal scroller. The wrap failed in that
document. The extra trees overflow off the layout. Use `ScrollExcludeDocuments` as above while
reporting it.

### More than four trees and the extra trees are off screen

`[Presentation] EnableScroll` is `false`, or the document is listed in
`ScrollExcludeDocuments`.

### `Ectype: HUD document has no ClassAbilityContainer/Scavenger element - HUD slot skipped (game update?).`

The HUD layout no longer has the element the role-ability slot is built from. Roles work
without a HUD slot. The same line with `HUD bar buttons are skipped this session` applies to
the ability bar.

### `Ectype: HUD ability-slot wiring failed (reported once; modded roles run without a HUD slot): <exception>`

The slot for a framework role's ability could not be built. The ability still works through
its key.

### `Ectype: HUD ability element has no '<name>' child - that layer keeps the donor's glyph (game update renamed it?).`

A layer of the slot element is missing. The role's icon does not show on that layer.

### `Ectype: HUD slot '<name>' for '<roleId>': glyph MISSING (donor or placeholder shows), siblings [...]`

Logged once per role under `VerboseLogging`. `glyph MISSING` means the hero perk's `icon` key
matched no embedded PNG. The slot shows the donor's art or the placeholder.

### `Ectype: HUD role icon install failed: <exception>` and `Ectype: lobby role icon install failed: <exception>`

The role's icon could not be drawn in the crew HUD or the lobby list. The role works.

### `Ectype: no loaded sprite named '<name>' - that pip layer is left blank.`

A stock sprite the scroller's page indicator borrows is not loaded. The indicator draws
without that layer.

### Role wording

| Line | Cause |
| --- | --- |
| `Ectype: role wording installed.` | Normal. A `(chained after <type>)` suffix means the game or another mod had its own table postprocessor; it still runs first. |
| `Ectype: role wording: no entry '<key>' in <table> (<locale>) - the game shows its own text there (game update renamed the key?).` | A listed string-table key no longer exists. That one place keeps saying "Class". |
| `Ectype: role wording: cannot list loaded string tables (Unity Localization update?) - tables already loaded keep the game's text until the language changes.` | Tables the game loaded before the framework were not rewritten. Changing language and back, or a restart, reloads them through the hook. |
| `Ectype: role wording (<what>) failed, reported once: <exception>` | One step of the rewrite threw. The game's own text shows where that step would have changed it; nothing else is affected. |

"Class" still shows after turning `[Presentation] RoleWording` on mid-session: the screen was
built before the switch. Reopen it, or restart. Non-English languages are never changed.

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
`RoleRegistry.RegisterAbilityFactory(roleId, factory)` at the plugin's `Awake`. The framework
builds one per character spawn, installs it into the character's Opsive locomotion, and points
it at the role's hero, row-5 and row-7 banner perks.

### `Ectype: ability factory registered for '<roleId>'.`

The factory call arrived. If the line is absent, the call never ran: check plugin load order
and the `[BepInDependency]` attribute. A factory registered for a `roleId` with no manifest
does nothing.

### `Ectype: ability factory for '<roleId>' threw - the role stays data-only this session: <exception>`

The factory threw on a character spawn. The role has no ability this session and its perks
still apply. Fix the exception.

### `Ectype: ability factory for '<roleId>' returned <null or a type>, not an EctypeRoleAbility - the role stays data-only this session.`

The factory must return a new `EctypeRoleAbility` subclass instance. The framework rebuilds a
character's ability array as the abilities that are not role abilities, in their order,
followed by each role's `EctypeRoleAbility`; an ability of any other type would be kept in the
first part and appended again by every rebuild, so it is refused.

### `Ectype: ability field wiring failed for '<roleId>' (member rename? see warnings above) - ability not installed.`

One of the identity fields on the ability (`AbilityPerk`, `CapstonePerk`,
`SuperiorAbilityPerk`, `abilityUsable`, `tags`) could not be set. The warning above the error
reads `field '<name>' not found on <Type> (game update?)` and names the missing field. A game
update renamed it.

### `Ectype: installing '<roleId>' ability into the locomotion failed: <exception>`, `Ectype: ability install sweep failed: <exception>`

Installing the ability into the character failed. That character has no ability from the role
until it respawns. Other characters are unaffected, and so are the other roles: every role's
ability is installed on its own, and when the shared array rebuild throws, the new abilities
are added back one at a time so only the role whose ability throws is skipped. With
`VerboseLogging` on, that retry logs `Ectype: one rebuild for <n> role ability(ies) threw;
retrying one role at a time: <message>`. `Ectype: rebuilding the ability array after a failed
install failed: <exception>` means the final rebuild without the failed ability threw too; the
character's ability array may still hold it until the next spawn.

### `Ectype: could not read m_CharacterLocomotion off a ClassAbility - game/Opsive update? Modded abilities will not install.`

The field the framework reads the locomotion from is gone. No framework ability installs.

### `Ectype: <AbilityType>.OnApplyEffect threw (reported once): <exception>` and `... OnRemoveEffect threw ...`

An override on an `EctypeRoleAbility` subclass threw. The base class catches the exception so
Opsive's update loop keeps running, and logs it once per ability type. Fix the override.

### `Ectype: <AbilityType> channel <how it ended>, cooldown <n> s.`

Logged with `[Debug] VerboseLogging` on, by the owning client, once per channel end. `ran its
full <n> s` is a channel that ran out. `cancelled after <used> of <n> s (<cause>)` is an end
from the ability's cancel policy, and `stopped after ...` an end from outside it: a deselect,
a despawn. `scaled to the share used` marks a partial cooldown.

### `Ectype: <AbilityType> cancel policy: <step> threw (reported once): <exception>`

One step of the cancel policy failed: a trigger's check, a subscription, the deferred stop or
the early-end cooldown. A trigger that threw records nothing, so the channel runs on until
another trigger or its own end stops it. A failed cooldown rewrite leaves the full cooldown.
A `CollisionSpeed` callback that throws lands here, named as the collision check.

### `Ectype: <AbilityType>.RequestCancel ignored: its CancelPolicy does not list CancelTriggers.Requested (reported once).`

The ability called `RequestCancel` with no policy, or with a policy whose `On` lacks
`Requested`. Add `CancelTriggers.Requested` to `On`.

### `Ectype: <AbilityType>.OwnerCanUse threw (reported once; usability left as it was): <exception>`

The ability's usability override threw. `AbilityUsable` keeps its last value until a later
call returns. Fix the override.

### `Ectype: could not repoint <VanillaAbilityType> onto '<roleId>' (field rename? see warnings above) - that role has no active ability.`

A role suppresses a stock tree and takes over the stock ability that tree owned. The repoint
failed because a field was renamed. The role has no ability.

### `Ectype: ClassAbility.VerifyIfIsSelectedAbility not found - game update? Adopted abilities will not select until the next loadout change.`

An adopted stock ability selects only after the next loadout change.

### `Ectype: deciding the capstone payload for <AbilityType> failed - the vanilla payload stays, and it may stack with the role's authored effects: <exception>`

A role that adopted a stock ability and authored its own capstone effects may receive both.

### Things that look like they should work

- **The ability key does nothing for the first seconds after selecting the perk.** Selecting
  the hero perk, and every respawn, starts the full cooldown, as for any role ability.
- **Role code writes `AbilityUsable`.** The framework decides it again on the owning client's
  next idle tick, so the two writes alternate and each raises the HUD's usable-changed event.
  Override `OwnerCanUse()` instead.
- **A subclass overrides `Update`, `InactiveUpdate`, `AbilityStarted`, `AbilityStopped`,
  `ActivationKeyPressed` or `OnDestroy` without calling its base.** The cancel policy and the
  usability check run from those methods. Without the base call, cancels are never stopped,
  the triggers are never unsubscribed, or usability is never decided.
- **The ability key does not cancel a channel.** `On` lacks `CancelTriggers.AbilityKey`.
  Without it a press while the channel runs goes to the game's own start, which refuses.
- **An ability added at runtime works in solo and breaks other players.** Opsive replicates
  ability start and stop by position in the character's ability array. A client whose array
  differs resolves the number against the wrong ability or past the end of the array, and the
  exception lands inside the engine's own tick on every other client, once per start and once
  per stop. Register the ability with `RuntimeAbilities.Register(ability, key)` after it is in
  the array, with a literal key namespaced by your mod. The framework then replicates it by key
  on its own channel. A framework role ability is registered automatically as
  `ectype:<roleId>`.
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
| A screen that lists individual assets | The game's asset check | One side registered required content (a role's tree or perks, for example) that the other did not. |
| The same screen naming `Ectype Framework <version> (network contract <n>)` | The game's asset check | The two frameworks speak different network contracts. The name is the host's framework. |
| Removed from the room after arriving, with no message from the framework | VoidManager, on the host | Same version, different DLL bytes. Only the host compares hashes. |
| Removed at once, chat says `Kicked player <name> for not having mods.` | VoidManager, on the host | The joiner has no VoidManager and the host runs a mod declared `All` to VoidManager (a role mod, for example) or has turned VoidManager's progression switch off. VoidManager's policy: install VoidManager, or host without it. |

Fix every case the same way: install the host's exact versions. The Void Crew Mod Launcher
does this for a joiner when the host enabled the mods in it; without the launcher, match each
version by hand. Raise the version of any mod whose bytes changed.

### Everyone has the same version and one player is still refused

The two DLLs differ. A dependent records the framework assembly version it was compiled
against, so a locally rebuilt dependent differs from a downloaded one at the same version
number. Publish the build to Thunderstore under a new version and have everyone install that
version.

### A player lacks a role mod and the room runs

It does not. The asset check refuses the join in both directions. See `multiplayer.md`.

### A role is suppressed and players still need it

Things that look like they should work and do not:

- **A suppressed role still has to be installed by everyone.** Suppression through another
  role's `suppressRoles` hides a role and leaves its assets registered, so the asset check
  still compares them.
- **Suppression does not delete saves.** A suppressed role's perks stay in each loadout and
  read as absent. Removing the role mod that suppresses it brings the build back.
- **There is no in-game switch.** Which roles are suppressed follows from the installed role
  mods' manifests alone. To change it, change the installed mod set.

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
| `Ectype: dropped ability-sync start '<key>' for view <id>: the character had no look source after 5 s.` | A start arrived for a character on this client that never became able to run an ability, so it was not started here. The owner's next start replicates normally. |
| `Ectype: ability-sync send failed: <exception>`, `Ectype: handling ability-sync message failed: <exception>`, `Ectype: late-join ability-sync failed: <exception>`, `Ectype: resolving ability-sync recipients failed: <exception>` | A step of the keyed channel threw. A registered ability never falls back to the index RPC, so a failed send is a dropped state update. |

A registered ability reaches only peers whose player property `Ectype` carries this build's
network contract. A peer without it sees no message it cannot act on and ignores keys it has
no ability for.

### The framework's network channel

Ability replication and enemy-status replication ride one Photon event, code 181. Every
message starts with a subtype byte: 1 is an ability start or stop, 2 an enemy status apply or
remove.

| Line | Cause |
| --- | --- |
| `Ectype: listening on Photon event 181 (<n> message type(s), network contract <c>).` | The receive path is subscribed. Logged once, on the first frame. Without it no framework message is received on this client. |
| `Ectype: net send <name> (subtype <s>) to actor(s) <list>.` | VerboseLogging only. One per message sent, naming its recipients. |
| `Ectype: net recv <name> (subtype <s>) from actor <a>.` | VerboseLogging only. One per message received, naming the sender's actor number. |
| `Ectype: published player property 'Ectype' = <c> for the next join.` or `... to the room.` | VerboseLogging only. The local player now carries the property other clients filter recipients by. |
| `Ectype: ignored net subtype <s> from actor <a>: this build has no handler for it (a different network contract?).` | Logged once per subtype. The sender runs a framework on another network contract. The contract marker normally refuses such a peer at join. |
| `Ectype: ignored a malformed net event from actor <a> (no subtype byte).` | Logged once. Something else raised event 181 with another payload: another mod using the same code. |
| `Ectype: network contract <c> marker registered (<guid>, effect <effect>).` | The contract marker asset is in the game's registry. |
| `Ectype: player '<name>' (actor <a>) runs a framework on network contract <n>, this one speaks <c>: no ability or status messages are exchanged with them.` | Logged once per player. The join check did not refuse them, which happens in a build made with `-p:NoProgressionFlag=true` (its marker is outside the compared set) or with the framework disabled on one side. Install the same framework on both sides. |
| `Ectype: ignored net <name> (subtype <s>) from actor <a>: its fields match no declared layout` | Logged once per sender and subtype. A message with the wrong field types; the sender runs another layout under the same contract number, which is a framework bug. |
| `Ectype: net <name> (subtype <s>) not sent: its arguments match no declared layout` | An error in the framework itself; reported once per subtype. |
| `Ectype: net <name> (subtype <s>) send refused by Photon (not connected?)` | Photon would not queue the event. The verbose `net send` line ends in `FAILED` for each such send. |

To check replication between two clients, turn on VerboseLogging on both, activate a
registered ability on one and apply an enemy status on it. The sender logs one `net send` line
per message and the other client one `net recv` line naming the same subtype and the sender's
actor number. A `net send` with no matching `net recv` means the receiver is not subscribed;
no `net send` at all means the receiver is not in the recipient list, because its `Ectype`
property is missing or carries another contract.

### Rooms with more than one ship

Each check below is a log line a tester can look for. The verbose ones need `[Debug]
VerboseLogging` on, on every client compared.

| Line | What it shows | Check |
| --- | --- | --- |
| `Ectype: topology: <n> ship(s), provider=<single\|fleet>[, <k> dying][, numbers from <VCF_Ships\|view-id rank>].` | The settled count of living ships, about two seconds after each mission start and each ship loss. `provider=fleet` means Void Crew Fleet's API is bound and the Fleet plugin is enabled; the `numbers from` tail then says whether ship numbers come from Fleet's published list or from spawn order. | A vanilla mission logs `1 ship(s), provider=single.` A three-ship Fleet mission logs `3 ship(s), provider=fleet, numbers from VCF_Ships.` on every client. |
| `Ectype: topology: Void Crew Fleet <version> bound, API v<n>[; missing <members>, answered from game data instead].` | Fleet was found at start-up. The tail names API members this Fleet lacks; those answers come from game data. | Logged once per launch with Fleet installed. |
| `Ectype: topology: Void Crew Fleet <version> has API v<n>; this framework reads v1, so answers come from game data alone.` | Fleet's API version is one whose members this framework was not written against, so it is not bound. | Update the framework to one that reads that version. |
| `Ectype: topology: Void Crew Fleet <version> is loaded but has no VoidCrewFleet.FleetApi; answers come from game data alone.` and `Ectype: topology: binding Void Crew Fleet's API failed; answers come from game data alone: <message>` | Fleet is installed but its API could not be found or bound. | Crew ships then come from the aboard rule, so a player standing on another ship resolves to it. |
| `Ectype: topology: Fleet's VCF_Ships room property failed its shape check (<why>); ship numbers come from view-id rank instead.` | Fleet's published ship list was not an array of one to six view ids, or named one id twice, so ship numbers come from spawn order. | Numbers can then differ on a client that joined after a ship was lost. |
| `Ectype: characterShip - actor <n> → ship <k> (<source>).` or `... → no ship (<source>).` (verbose) | Where one holder's `characterShip` contribution sits, and which rule decided: `single`, `only living ship`, `fleet crew`, `aboard, same faction`; or why it is pending: `single, other side's ship`, `only living ship, other side's ship`, `single, no ship`, `no living ship`, `fleet: not playing`, `fleet ship <n> not alive`, `pending`. | With holders on two ships, every client, the host included, logs the same `<k>` for each actor. A PvP boarder never resolves to the enemy's ship, and when one team's ship is the last one left, the other team's players log `other side's ship`. |
| `Ectype: IncomingProjectileSpeed: shot <id> → ship <k>, factor <f>.` (verbose; with one ship, only when the factor is not 1) | The ship a hostile shot was bound to as it registered, and the factor it carries. | Two ships with different factors: a shot aimed at each carries its own ship's factor, and the same `<id>` logs the same `<k>` on both clients. |
| `Ectype: IncomingProjectileSpeed on ship <k> now <f>.` (verbose) | One ship's factor changed; its shots in flight take the new value. | Only that ship's shots change speed. |
| `Ectype: status marker lock gate bound to ship <k>.` (verbose) | The ship whose target locks gate lock-only status markers on this client. | In co-op, walking from one ship into another logs the new ship. |
| `Ectype: fleet arrived from the void (<n> ship(s), <kind>, server <ms>, <k> subscribers).` (the process's first at `[Info]`, later ones verbose) | `ShipArrivals.FleetArrived` fired. | With `VerboseLogging` on, a three-ship jump logs three `ship arrived from the void` lines and one of these on each client. |
| `Ectype: topology: ship <k> started dying.` (verbose) | The death-start broadcast reached this client. At the next pass every holder on that ship resolves again by the crew-ship rules: with Fleet active they go pending; without it, to a single living ship on their side, or to the same-faction ship they stand on. | Logged once per lost ship on every client. |
| `Ectype: Topology.Changed subscriber <Type>.<method> threw: <exception>` | A subscriber to `Topology.Changed` threw. Each runs in its own `try` block, so the others still ran. | |
| `Ectype: topology ship registration failed: <exception>`, `Ectype: topology ship removal failed: <exception>`, `Ectype: topology ship death handling failed: <exception>` | A ship lifecycle patch threw. The registry can then miss a ship or keep a destroyed one until its next prune. | |
| `Ectype: characterShip rebuild for a ship change failed: <exception>` | Moving one holder's contribution to its newly resolved ship threw. That holder is retried at the next pass. | |
| `Ectype: dev self-test: timed stat copy passed - DeepCopy returned; <n> objects walked, no StatCollection, no Unity object other than a ScriptableObject; copy of applied value 0.25 then 0, copy of template 0.25 then 0 (expected 0.25 then 0).` (dev tools build only) | A stat modifier carrying a live timed-stat value survives the game's deep copy without dragging in the target collection, and both kinds of copy follow the curve and go neutral at the end. A `FAILED` line names what the copy reached or which amount was wrong. | Logged once per launch, a few seconds after a character exists. The test names a ship-only stat on a character, so it also logs one `has no effect there` warning. |
| `Ectype: timed stat copy check on apply failed (reported once): <exception>` | The check that finds copies of a timed-stat modifier as a collection applies them threw. Copies of applied values may then keep the amount they had when copied. | |
| `Ectype: timed stat '<label>': refreshing a copy threw; the copy is no longer refreshed: <message>` (verbose) | A copy of a timed-stat modifier could not be recalculated, usually because its collection went away. | |

## Utility APIs

These lines come from the APIs a non-role mod calls.

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

### Session data and host-synced settings

| Line | Cause and fix |
| --- | --- |
| `Ectype: session data: patched.` | The hooks on the game's session JSON are in. Without this line no host value reaches a client, and `HostValue` follows each client's own config. |
| `Ectype: session data: GameSession.AsJObject / FromJObject not found (game update?); session data and host values follow each client's own config.` | A game update renamed the session serialisation. Every client then uses its own config. |
| `Ectype: session data: PhotonService.OnEvent(EventData) not found (game update?); the session data's sender is not checked.` | The envelope is accepted from whoever raised the session event. |
| `Ectype: session data: '<key>' not registered: the session-data patches are not applied this session, so no host value reaches this client.` | The framework is inert or the hooks are missing; see the lines above. |
| `Ectype: session data: Register('<key>') refused: <problem>.` | The key is empty, longer than 128 characters, holds a control character or is already registered, or no producer was given. |
| `Ectype: session data: sending <n> key(s) with <hub\|mission> session <id> (<bytes> bytes[, <k> restored from the saved run]).` | Host: the values that went out with the session. A client that logs no matching `received` line did not get the session event. |
| `Ectype: session data: received <n> key(s) from the host (actor <a>) with <hub\|mission> session <id>.` | Client: the host's values arrived, as the session started or, for a late joiner, as it joined. |
| `Ectype: session data: key '<key>' is <n> bytes, over its <cap>-byte cap; left out of this session.` and `... would bring the envelope to <n> bytes, over the 16384-byte cap; left out of this session.` | Host: the value is too big for its own cap or for the shared envelope. Shrink it or raise `SessionDataOptions.MaxBytes`. |
| `Ectype: session data: producing key '<key>' threw; left out of this session: <exception>` and `Ectype: session data: applying key '<key>' threw: <exception>` | The mod's producer or `apply` threw. Other keys are unaffected. |
| `Ectype: session data: key '<key>' from the host does not read as <Type> within <cap> bytes; this client keeps no value for it.` | The host runs another version of the mod, with another type for the key. Logged once per key. |
| `Ectype: session data: ignored the host's envelope: <problem>.` | The envelope is over 16384 bytes, not an object, or in a format this framework does not read. |
| `Ectype: session data: ignored the session data from actor <a>, who is in the room and is not the master client (master is actor <m>).` | Another player raised the game's session event. The game still loads that session; the framework keeps none of its values. |
| `Ectype: session data: saved-run sidecar for session <id> unreadable; values are produced fresh: <message>` and `Ectype: session data: saved-run sidecar write failed (<path>): <message>` | The sidecar folder `BepInEx/config/jack.voidcrew.ectype.sessions` could not be read or written. A resumed run then gets fresh values. |
| `Ectype: host value '<key>' is already bound to another config entry; this one follows the local config only.` | Two `HostValue` calls named one key for different entries. Give one an explicit key. |
| `Ectype: host value '<key>': ignored a room-property write from actor <a>, who is not the master client (<m>).` | Client: another player wrote the host's room property. The client keeps the value it had; the host writes its own back. |
| `Ectype: host value '<key>': actor <a> wrote the room property; republishing the host's value.` | Host: the same write, seen from the host. |
| `Ectype: host value '<key>' is over the 4096-byte cap; not published in the hub.` | Host: the config value is too big for a room property. Clients read it from the session data from the next session start. |
| `Ectype: host value '<key>': the host's room value does not read as <Type>: <message>` | The host runs another version of the mod. |
| `Ectype: host value '<key>' published to the room[ (the room held another value)].` and `Ectype: host value '<key>': room property not used (<reason>).` (verbose) | The host wrote its value; a client found the property absent, stamped by an actor other than the master, over the cap or not JSON. |
| `Ectype: dev self-test: session data passed (round trip through GameSession.AsJObject/FromJObject, 5 keys, <n> bytes; size caps; sidecar write/read/prune; host-value stamp).` (dev tools build only) | The envelope survives the game's own session serialisation, the caps refuse by key name, and sidecars write, read and prune. A `FAILED` line names each difference. Logged once, on the first frame. |

A client that keeps its own config for a `HostValue` (`IsFromHost` false) in the hub has no
valid room property from the current master; in a mission, the session it is in carries no
value for the key, usually because the host does not run the mod.
[reference/session-data.md](reference/session-data.md) has the precedence.

### Mod-declared stats and session stats

| Line | Cause and fix |
| --- | --- |
| `Ectype: mod stats: <n> declared in <m> file(s): <owner/name>=<id> (<collection>), ...` | The stats files were read and these stats registered. A reader computing the id by formula gets the same numbers. |
| `Ectype: mod stats: none declared (<m> file(s) read).` | Stats files were found and none of their stats registered. The errors above say why. With `VerboseLogging` the line also appears with 0 files. |
| `Ectype: mod stats: <declarer> rejected: <problem>.` | The whole file was refused: not JSON, not an object, no `owner` or an owner outside lower-case letters, digits, `.`, `_` and `-`, a reserved owner (`ectype`, `framework`), no `stats` array or more than 256 entries, larger than 65536 bytes, or unreadable. `<declarer>` is `<assembly> resource <name>` or the file's path under `plugins/`. |
| `Ectype: mod stats: <declarer> stat #<i> ('<name>') skipped: <problem>.` | One stat was refused and the rest of the file stands: a bad name, a `kind` other than `field` or `factor`, a field with no `default`, a factor whose `default` is not 1, a non-finite default, a `collection` other than `session`, `ship` or `character`, a `displayType` other than `value` or `percentage`, or a non-boolean `lowerIsBetter`. |
| `Ectype: mod stats: <declarer> declares '<owner/name>' (id <id>), already declared as '<owner/name>' (id <id>) by <declarer>; the later declaration is skipped.` | Two declarations of one id, or of one name in different capitals. Embedded files are read before loose ones, so a mod that ships its file both ways declares every stat twice: ship one copy. |
| `Ectype: mod stats: <declarer>: unknown key '<key>' ignored.` and `Ectype: mod stats: <declarer> stat #<i> ('<name>'): unknown key '<key>' ignored.` | A key the framework does not read, often a misspelling. |
| `Ectype: mod stats: registering '<owner/name>' from <declarer> failed, skipped: <message>.` | The game's stat table refused the id: another custom stat derives it, or a role manifest declares the same owner and name. |
| `Ectype: mod stats: '<owner/name>' iconFrom '<name>' gave no icon: <message>.` | `iconFrom` names no vanilla or framework stat. The tooltip shows no icon. |
| `Ectype: mod stats: <character\|ship\|session> surface installed on <type> (<owner/name>=<base>, ...).` | The first collection of that kind received its stats. Logged once per kind. |
| `Ectype: mod stats: registering <character\|ship\|session> stats onto <type> failed - readers get the declared defaults: <exception>` | Registering into one collection threw. That object's readers fall back to their constants. |
| `Ectype: mod stats: '<name>' names no stat (expected "owner/Name" or a framework stat name); it reads 0.` | `ModStats.Get` or `TryGet` was given a name that is neither. Logged once per name. |
| `Ectype: mod stats: discovery failed: <exception>` | Reading the stats files threw. No mod-declared stat is registered this launch; the framework's own are. |
| `Ectype: session stats: opened for session <id> as host (<name>=<value>, ...).` | Host: the session's collection, with the values published to every client. |
| `Ectype: session stats: opened for session <id> with the host's values (<name>=<value>, ...).` | Client: the host's values arrived and the collection reproduces them. Compare with the host's line. |
| `Ectype: session stats: opened for session <id> at the declared defaults (<name>=<value>, ...).` | The session carried no session stats. One of the two lines below says why. |
| `Ectype: session stats: session <id> carried no session stats; every session stat reads its declared default.` | Client: the host's session data had no `framework/sessionStats` key. |
| `Ectype: session stats: session <id> was sent without its session stats; clients read the declared defaults.` | Host: the values were produced and the session data left them out; an error from session data above names the cause. The host keeps its own values. |
| `Ectype: session stats: <name> changed after the session started; clients keep the session-start value until the next session.` | Host: a contribution outside `SessionStats.Opening`. Contribute from `Opening` for the value to reach clients. Logged once per stat per session. |
| `Ectype: session stats: a contribution on a client was refused; only the host contributes, and clients receive the host's values.` | `SessionStats.Contribute` ran on a client. Gate the call on the master client. Logged once. |
| `Ectype: session stats: a contribution with no session open was refused; contribute from SessionStats.Opening.` | `Contribute` ran outside a session. Logged once. |
| `Ectype: session stats: stat <name> is not a session stat; the contribution was refused.` | The id is not registered in the session collection: a character or ship stat, or a stat whose stats file was refused. |
| `Ectype: SessionStats.Opening subscriber <type>.<method> threw: <exception>` | A mod's `Opening` handler threw. Its contributions up to the throw stand; the other subscribers ran. |
| `Ectype: session stats: inert this session (session data unavailable); every session stat reads its declared default.` | The session data hooks are not applied; see the session data lines above. |
| `Ectype: session stats install failed: <exception>` | The session collection could not be set up. Every session stat reads its declared default. |
| `Ectype: session stats: removing a modifier at session end threw: <message>` | A modifier's own cleanup threw as its session ended. The collection is dropped anyway. |
| `Ectype: session stats: closed for session <id>.` (verbose) | The session ended or the client left the room. |
| `Ectype: dev self-test: mod stats passed (id formula; data file parse, duplicate and charset checks; session collection contribute, publish, rebuild and dispose).` and `Ectype: dev self-test: generic framework stats passed (5 at their documented ids).` (dev tools build only) | The id of `fleet/HackSeconds` is 714605015, a good file reads back, duplicates and bad names are refused, a client collection rebuilt from the host's values reads the same numbers, and the five generic factors sit at their documented ids. A `FAILED` line names each difference. |

[reference/mod-stats.md](reference/mod-stats.md) has the file format, the id formula and the
session collection's lifecycle.

### Field-backed stats

| Line | Meaning |
| --- | --- |
| `Ectype: wiring <label> as a stat failed - the field keeps its authored number: <exception>` | The stat could not be attached. Perks that name it do nothing. |
| `Ectype: ChannelGrapplingHook.<name> not found (game update?) - that grapple stat is registered nowhere and perks written against it do nothing.` | A field the framework wraps no longer exists. |
| `Ectype: BuildProcess.StateTimer is not reachable - SystemModuleConstructionSpeed is inert: <exception>` | The module install-speed stat has no effect. |
| `Ectype: applying SystemModuleConstructionSpeed failed (suppressing further reports): <exception>` | Advancing a build process threw. Reported once per session, since it runs every frame for every process. That process keeps the game's own speed. |
