# Ectype role manifest: field reference (contract v2)

The complete authoring surface for `ectype-role.json`, the JSON document a role mod embeds
in its assembly. Field names are exact JSON keys. A JSON Schema for editors is at
[schemas/ectype-role.schema.json](schemas/ectype-role.schema.json). How a role mod is put
together, and what the framework does with the manifest, is in
[reference/roles.md](reference/roles.md). The smallest working manifest ships with the
data-only example under [../examples/](../examples/).

A manifest that fails validation rejects its role by name in the log and nothing else
breaks. Validation runs in this order: `minFrameworkVersion`, `contractVersion`, `roleId`,
`roleName`, `hero`, then each perk, custom stat and `suppress` entry.
[troubleshooting.md](troubleshooting.md) lists each message.

## Top level

| Field | Required | Meaning |
| --- | --- | --- |
| `contractVersion` | yes | Must equal the framework's contract version (currently 2). The check is exact equality, so a higher or a lower value rejects the role, and an omitted field reads as 0 and is rejected. A rejection names the role; nothing else breaks. |
| `minFrameworkVersion` | no | The oldest framework version the role works with, `"major.minor.patch"`. A framework older than this rejects the role by name. An unparseable value also rejects. Absent means no minimum. |
| `roleId` | yes | Namespaced stable id, conventionally the same string as the plugin GUID (for example `acme.voidcrew.warden`). It seeds every derived GUID and is the ordinal sort key across all installed role mods. Never rename after shipping.|
| `roleName` | yes | Display name. Shown upper-cased as the tree title and in the role tooltip. Can be renamed after shipping.|
| `roleDesc` | no | Role tooltip body. Rich text (`<color=#FFD700>` and so on) is allowed. |
| `roleIcon` | no | Icon key for the role's icon (see Icon keys below): the glyph the crew HUD and the lobby room list draw beside a player of this role, the counterpart of the game's `RoleIconGunner` and the other stock role icons. Author it like those: 256x256, white on transparent, bold enough to read at the crew HUD's size (the slot is 16px tall, so a 256 canvas draws at 1/16 scale and a 16px stroke becomes one pixel). The stock glyphs span 155-182px of their 256 canvas. The framework fits the sprite rect to the glyph's alpha bounds, a square the glyph spans 70% of, so a glyph authored with a wider margin still draws at the stock scale; a glyph already spanning 70% or more keeps the full canvas. The same fitted sprite is the gene tree's own `ContextInfo` icon. Omitted: the HUD and lobby show the framework placeholder, and the tree icon falls back to `roleIconDonor`. |
| `color` | no | Accent colour, `"#RRGGBB"`. It becomes the role's USS custom-property family (`--ui-color__perk-<role>` plus derived variants), defined and wired into every consumer stylesheet by the framework, so colouring runs entirely through the game's own cascade. Omitted or unparseable: the tree renders white and the framework logs a warning. Also warned when R+G+B >= 700 of 765 (too close to white) and when nearly identical to another installed role's accent (componentwise distance < 90 of 765, which reads as the same colour at gene-tree scale). Pick something distinct from the stock four (Gunner red, Pilot blue, Engineer green, Scavenger purple) and from other role mods you expect to be installed alongside. |
| `hero` | yes | The row-0 role banner; a PerkSpec. `key` defaults to `"hero"`; `row`, `col` and `wide` are forced to 0, 0, true regardless of what is authored. Give it a real effect. Vanilla heroes are the role ability (a role mod's registered ability factory supplies one; see [reference/roles.md](reference/roles.md)), but heros do not require active ability code. |
| `perks` | no | List of PerkSpec. Partial grids are legal and common. Does not enforce traditional layout.|
| `customStats` | no | List of CustomStatSpec. A custom stat is inert until code reads it, so do not ship one in a data-only role. Role code gets the id from `RoleStats.Id(roleId, name)`. `RoleStats.RegisterAbilityTiming` registers an ability's own duration and cooldown under two of them, scoping a perk to that ability where `ClassAbilityDuration` and `ClassAbilityCooldown` reach every role ability on the character. |
| `suppress` | no | List of vanilla perk-tree GUIDs (32 hex chars; dashes allowed) whose trees should not exist while this role is installed: the "this role replaces Gunner" mechanism. The union across every installed role mod applies. A suppressed tree is removed from both terminal documents (freeing its layout slot), skipped by the role tally, and its perks are scrubbed from saved loadouts with vanilla refund semantics: the spent points return as unspent on the next profile write. An entry that names a single perk, a framework-registered role (use `suppressRoles`), or nothing in this game version is ignored with a named warning, and an ignored entry never fails the role. An entry that is not a GUID at all (not 32 hex chars once dashes are removed) rejects the role. A dev tools build of the framework ([CONTRIBUTING.md](../CONTRIBUTING.md#dev-tools-build)) writes each tree's `guid` to `BepInEx\EctypeDump\vanilla-roles.json` with the `[Dev] WriteVanillaDump` switch on. The stock four: Gunner `b03e6f1f9a2ec3c4cae3b5027026e4e1`, Pilot `1bfec4ad20c77c34cbb1b77834afd960`, Engineer `382e55cd4fc760e4ab8bc79b09f23063`, Scavenger `7a490a7247bc6334fbeb24a0ddc48a80`. |
| `suppressRoles` | no | List of other framework roles, by `roleId`, whose trees should not exist while this role is installed and not itself suppressed. The tree is kept out of both terminals, its perks stop applying and their points count as unspent (the perks stay in each saved loadout, read as absent, so removing this role restores the build), and its payloads are not re-homed. An id that is not installed is ignored with a warning. The role's own id is logged as an error and ignored, and the role still registers. A cycle (A lists B, B lists A) is broken in `roleId` order with a warning. |

## PerkSpec

| Field | Required | Meaning |
| --- | --- | --- |
| `key` | yes | Stable id within the role. The perk GUID derives from (roleId, key), so renaming a key orphans saved selections unless `guid` pins one. Case-insensitive, unique per role. |
| `guid` | no | Explicit 32-hex GUID overriding derivation. Use it to survive a key rename. |
| `name` | yes | Display name. |
| `desc` | no | Tooltip body. Rich text allowed. The CURRENT/NEXT LEVEL block renders from the StatMods automatically, so do not duplicate per-level numbers into prose unless the effect has no StatMod. |
| `icon` | no | Icon key (below). Omitted: the donor icon. |
| `iconDonor` | no | A `PerkBuffContainer` GUID (32 hex, dashes allowed) whose `ContextInfo` icon this perk borrows when `icon` names nothing loadable. The sprite is shared by reference and never copied, so a perk modelled on a stock one shows the exact stock art with no PNG extracted, embedded or redistributed, and it tracks the game's own art across updates. Unset or unresolvable falls through to the framework placeholder. |
| `row` | yes | 1-7 (row 0 is the hero's). Every tree is a copy of the stock grid, 8 rows shaped `[1,3,1,3,3,1,3,1]`: rows 1, 3, 4 and 6 hold three cells, rows 2, 5 and 7 are single wide cells (see `wide`). The manifest currently has no field to change the shape or its row gating. |
| `col` | rows 1/3/4/6 | 0-2 in the three-cell rows. Rows 2, 5 and 7 are single wide cells: use `col: 0`. Registration rejects only `row < 1`. The game's `PerkTreeVE.SetPerkData` indexes the cell map directly when the terminal builds: a `row` past 7, or a `col` past 2 on a three-cell row, throws `ArgumentOutOfRangeException`. On a single-cell row any `col` is forced to 0. |
| `wide` | rows 2, 5, 7 | Declares that the perk fills a wide cell. Rows 2, 5 and 7 are the wide cells (with the hero in row 0, the full-width cells of the stock grid). A perk in a wide cell must set `true`, otherwise the role is rejected with `perk '<key>' on row <n> is a wide cell and must declare wide: true`. `true` on any other row is rejected with `perk '<key>' on row <n> has wide: true, but only rows 2, 5 and 7 are wide cells`. Absent means `false`. The hero needs no flag. |
| `target` | no | Where the effects land. `"character"` (default): always on, on the player. `"seat"`: the manned module only, applied on sit and removed on stand. `"seatShip"`: the ship's stat collection while seated in any chair, the "all installed weapons" or ship-wide pattern (vanilla does "manning any System Module buffs all installed weapons" this way). `"characterShip"`: the ship's stat collection for every player holding the perk, seated or not (in a room with several ships, each holder's own crew ship), plus every ship module tree the game leaves detached from it, such as the Fabricator's; one contribution per holder, applied on every client. Case-insensitive. An unknown value skips the perk with a named error, and an unknown value on the hero rejects the role. A stat's category (see [stat-names.md](stat-names.md)) does not constrain the target. An effect whose stat matches nothing in the target's collection tree still registers and changes nothing; the framework logs one warning per perk, stat and target. Currently, a perk can only have one `target`.|
| `mods` | no | List of levels; each level is a list of effects. **Each level's list is the total at that level, not an increment.** The game applies only the current level's list, so a +10/+20/+30 ladder is authored as 0.10 / 0.20 / 0.30. |
| `levels` | no | Level (pip) count. The effective count is max(1, `levels`, `mods.Count`). Levels beyond `mods` register empty: they cost a point, do nothing, and warn on every launch. |
| `emptyOk` | no | Declares that every empty level of this perk is intentional (an ability perk whose payload is code, or an unlock-only perk). It silences the framework's per-launch "EMPTY StatMods" warning for this perk. Vanilla ships plenty of these deliberately: every hero, boost and superior banner, and every "Inscribe X" and "Fabricate X" perk. A level that does carry effects is unaffected either way. |
| `unlocks` | no | List of item GUIDs (32 hex, dashes allowed; anything else rejects the role) this perk unlocks in the Ship's Fabricator once bought at **any** level. This is the vanilla `UnlockCriteriaType.PlayerPerk` mechanism every "Inscribe X" and "Fabricate X" perk uses. The framework registers one `UnlockItemDef` per entry with `RequiredPerk` pointed at this perk's own GUID. `FabricatorModule` already listens for perk changes and grants matching items into the shared unlock pool itself, so no event hook is needed for that half. **This registers only the `UnlockContainer` side.** It does not make an item craftable and is not a way to add brand-new fabricator content: `CraftData.FillShop`, `FabricatorModule`'s own learnability check, and everything else that lists what the Fabricator can build all key off a separate container (`CraftingDataContainer` / `CraftableItemDef`), which `unlocks` never touches. The target item must already be independently craftable: a vanilla item, or a runtime-registered item that your mod also gave a `CraftingDataContainer` entry. Use `unlocks` to gate an already-existing craftable behind a perk. **The item must also not already have an `UnlockItemDef`** (a vanilla item, or one another mod registered). The framework can only add a fresh entry and never repoint someone else's, so a claimed GUID is skipped with a named warning and that perk grants no unlock. The exception is a claim held by a perk of a vanilla tree that this role's own `suppress` list removes: the framework repoints that unlock at this perk (a role adopts it also when another installed role suppresses the same tree). When two installed roles list the same item in their own `unlocks`, the first to register wins and the second is skipped with a named warning (see [troubleshooting.md](troubleshooting.md)). |

## EffectSpec (one entry in a level's list)

| Field | Meaning |
| --- | --- |
| `stat` | A `StatType` name from [stat-names.md](stat-names.md), a framework-owned stat, a custom stat declared in this manifest (it shadows a vanilla name for this role, so do not reuse one), or a raw int id. Case-insensitive. Unresolvable: that effect is skipped with a named error and the level registers without it. |
| `type` | `"percent"` (+X%, AdditiveMultiplier), `"scalar"` (xN, ExponentialMultiplier), `"flat"` (PrimaryAddend), `"flat2"` (SecondaryAddend), or an exact `ModifierType` name. |
| `value` | `percent 0.25` = +25%; `scalar 0.25` = x1.25; `flat` = the addend. **Negative values are legal and mean reduction**: `percent -0.20` = -20%, and the tooltip renders the sign (the game colours it by the stat's lower-is-better flag). |
| `kind` | `"float"` (default, builds a `FloatModifier`) or `"int"` (builds an `IntModifier`). Vanilla ships no bool modifier class, so a conceptually boolean stat (`JetpackEnhanced`) is still an int amount of 1. Getting it wrong still registers the effect (both are `PrimitiveModifier<T>`), but the tooltip renders the wrong format and, for a custom stat whose `displayType` does not match, may render nonsense. |
| `requiredTags` | List of tag names (for example `"Tag_DamageType_Kinetic"`, `"Tag_Module_Category_Weapon"`) this effect is filtered to. It maps directly onto the game's `ModTagConfiguration.RequiredTags`. Names resolve against the live `CsTagTable`; tags match **by object reference** in the game, so the framework looks up the same instance the game holds and never constructs one. Case-insensitive, and the `"Tag_"` prefix is optional (the game's own lookup strips it). An unresolvable name is dropped from the filter with a named warning, and the effect still registers, broader than authored. Omitted or empty: no filter. |
| `requiredTagCriteria` | `"and"` (default, every required tag must be present) or `"or"` (any one). Only meaningful with `requiredTags` set. |
| `requiredLocalTags` / `requiredLocalTagCriteria` | Same shape as `requiredTags` / `requiredTagCriteria`, mapping onto `ModTagConfiguration.RequiredLocalTags`. |
| `disablingTags` | List of tag names that disable this effect when present (`ModTagConfiguration.DisablingTags`). |
| `tagsToAdd` | List of tag names this effect adds while active (`ModTagConfiguration.TagsToAdd`). |

## CustomStatSpec

A custom stat gets a deterministic id under bit `0x20000000` (space the game never uses) and
a `StatStructure` in the live `StatTable`, so tooltips render it.

Declare one only for a number the framework does not already own; the framework's own
stats are listed in [reference/framework-stats.md](reference/framework-stats.md), and a perk
reaches one of those by naming it in an effect.

| Field | Meaning |
| --- | --- |
| `name` | Stat name, referenced from this manifest's effect lines. The id derives deterministically from (roleId, name). |
| `displayName` | Tooltip header; defaults to `name`. |
| `desc` | Tooltip body for the stat. |
| `icon` | Icon key. |
| `lowerIsBetter` | Colours reductions as buffs in tooltips. |
| `displayType` | `"value"`, `"percentage"` (default) or `"bool"`. |

## Icon keys

An icon key names an embedded PNG. The framework matches any manifest resource in the role
mod's assembly whose name **ends with** `<key>.png`, case-insensitively, so the plain file
name without extension works regardless of the build's namespace prefix. Embed with
`<EmbeddedResource Include="assets\*.png" />`. A key that matches nothing warns once and falls
back to the donor icon, then to the framework's placeholder (see
[reference/roles.md](reference/roles.md)).

## Economy facts for balancing

- Row gating: row N unlocks at 3 x (N-1) points spent in this tree (cumulative, counted across
  the rows above it; the hero's level counts).
- A player's total gene points scale with progression rank; a max-rank profile has about 31. The stock tree each have 27 purchaseable perk pips, but there is no upper or lower limit.

## Plugin boilerplate

A role mod's plugin carries these load-bearing parts:

- `[BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]` and
  `[BepInDependency("jack.voidcrew.ectype")]`. `jack.voidcrew.ectype` is the framework's
  BepInEx plugin GUID. BepInEx load order and dependency declarations use it, and it never
  changes. The dependency fixes load order so the framework discovers your assembly.
- A `VoidPlugin` subclass with `MPType` returning `MultiplayerType.All`, and `OnSessionChange`
  returning `SetMod_Session = true`. The multiplayer consequences are in
  [multiplayer.md](multiplayer.md).
