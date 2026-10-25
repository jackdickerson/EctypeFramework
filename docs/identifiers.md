# Identifiers

Every identifier a mod hands the framework is namespaced by the mod's BepInEx plugin GUID:

- **An owner field** takes the GUID itself: `"com.example.mymod"`.
- **A single-string id** takes `"<plugin GUID>/<name>"`: `"com.example.mymod/burn"`.

The GUID is the one the plugin's `[BepInPlugin]` attribute declares, spelled exactly, capitals
included. The name after the first `/` is the mod's own choice and may hold any character,
another `/` included. A whole id is at most 128 characters.

Two mods cannot collide on an identifier because BepInEx refuses to load two plugins with one
GUID. A reader of a log line, a saved run or a network message can tell which mod an identifier
belongs to by reading it.

## The check

The framework checks that the owner, or the part of an id before its first `/`, is a key of
`BepInEx.Bootstrap.Chainloader.PluginInfos`, the table of loaded plugins. The chainloader adds a
plugin's entry before it creates the plugin's component, so a plugin's own GUID is in the table
during its `Awake`. A plugin that loads after the caller is not there yet: name your own GUID,
not another mod's.

The GUID must also have the form BepInEx accepts, letters, digits, `.`, `_` and `-`, and must not
be a reserved owner.

| API | Field | A non-conforming identifier |
| --- | --- | --- |
| Stats file ([reference/mod-stats.md](reference/mod-stats.md)) | `owner` | The file is refused. An embedded file must name a plugin its assembly declares; a loose file must name a plugin whose DLL is in its folder. |
| `SessionStatMutators.Register` | `MutatorSpec.Owner` | Refused: `Failed`, with the reason. |
| `SessionData.Register`, `SessionData.HostValue` | key | Refused: `false`, or a host value that follows the local config only. |
| `InteractionGate.Register` | id | Refused: `false`. |
| `StatContributions.Register` | `StatContribution.Id` | Refused: `false`. |
| `RuntimeContent.Publish` | `ModContentEntry.Owner` | Refused: `Failed`, with the reason. |
| `CharacterStatuses.Register` | `CharacterStatusDef.Key` | One warning per id; it still works. |
| `EnemyStatuses.Register` | `EnemyStatusDef.Key` | One warning per id; it still works. |
| `TimedStatContributions.Start` | key | One warning per id; it still works. |
| `HudAbilityBar.Register` | `HudButtonSpec.Id` | One warning per id; it still works. |
| `RuntimeAbilities.Register` | key | One warning per id; it still works. |
| `LootTables.Register` | `LootDrop.Owner` | One warning per owner; it still works. |

## Exceptions

- **The framework's own owners.** `ectype` names the framework's own stats (`ectype/MeleeDamage`)
  and `framework` its own session data key. Both are reserved, matched ignoring case: a mod that
  uses either as an owner or id prefix fails the check.
- **A role's id, where the role's own content declares.** A role manifest's `customStats` and
  `mutators` are owned by the manifest's `roleId`, whatever the plugin's GUID. Code that registers
  through any API above uses the plugin GUID.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: <API> '<id>' does not follow the identifier rule: <problem>. It still works.` | An API that warns met a non-conforming identifier. Logged once per id. |
| `Ectype: interaction gate '<id>' refused: <problem>.` | An interaction gate id failed the check. |
| `Ectype: stat contribution '<id>' refused: <problem>.` | A lasting stat contribution id failed the check. |

The other refusing APIs carry the same `<problem>` text in their own refusal line. `<problem>` is
one of:

| Problem | Cause |
| --- | --- |
| `no plugin GUID before a '/'; expected "<plugin GUID>/<name>"` | An id with no `/`, such as `"mymod.burn"`. |
| `no plugin GUID before the '/'; expected "<plugin GUID>/<name>"` | An id starting with `/`. |
| `no name after the '/'; expected "<plugin GUID>/<name>"` | An id ending in its first `/`. |
| `the id is empty; expected "<plugin GUID>/<name>"` | An empty id. |
| `longer than 128 characters` | An id or owner over the cap. |
| `'<guid>' is not the GUID of a loaded BepInEx plugin; expected "<plugin GUID>/<name>"` | An id prefix that no loaded plugin declares. |
| `'<guid>' is not a BepInEx plugin GUID; expected "<plugin GUID>/<name>"` | An id prefix holding a space or another character BepInEx refuses in a GUID. |
| `'<guid>' is reserved for the framework; expected "<plugin GUID>/<name>"` | An id starting with `ectype/` or `framework/`. |
| `no owner; expected the registering plugin's BepInEx GUID` | An empty owner. |
| `owner '<owner>' is not the GUID of a loaded BepInEx plugin; expected the registering plugin's BepInEx GUID` | An owner that no loaded plugin declares. |
| `owner '<owner>' is not a BepInEx plugin GUID (letters, digits, '.', '_' and '-' only); expected the registering plugin's BepInEx GUID` | An owner with a character BepInEx refuses in a GUID, `/` included. |
| `owner '<owner>' is reserved for the framework; expected the registering plugin's BepInEx GUID` | Owner `ectype` or `framework`. |
| `owner '<owner>' is not the GUID of a plugin <its assembly declares\|in its folder>; expected '<guid>'` or `expected one of '<guid>', ...` | A stats file naming a plugin other than its own. |

## Things that look like they should work

- **`PLUGIN_GUID + ".burn"`.** No `/`: the whole string is read as the GUID, and no plugin has it.
  Write `PLUGIN_GUID + "/burn"`.
- **A short name such as `"mymod"` as the owner.** Not a loaded plugin's GUID.
- **The GUID in other capitals.** The comparison is exact: `Com.Example.MyMod` is not
  `com.example.mymod`.
- **The mod's display name, or its Thunderstore name, as the owner.** Only the `[BepInPlugin]`
  GUID is in the plugin table.
- **Another mod's GUID, to register on its behalf.** Fails when that mod loads after the caller,
  and makes the identifier claim an author it does not have.
- **A role mod's code registering under its `roleId`.** The exception covers the manifest only.
  The role's plugin GUID passes everywhere; a `roleId` that equals the GUID passes too.
