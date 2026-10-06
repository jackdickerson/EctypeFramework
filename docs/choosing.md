# Choosing what to build

A mod that depends on EctypeFramework takes one of three shapes. The shape sets what the mod
can do, what the author maintains and what every player in a room has to install.

| | Data-only role | Role with code | Utility-only mod |
| --- | --- | --- | --- |
| What it adds | A role: gene tree, hero perk, perks with stat effects | The same, plus a role ability and any behaviour the perks need | Behaviour with no role: statuses, loot, HUD buttons, stats, ship events |
| Author writes | A JSON manifest, PNG icons, a short plugin | The same, plus C# against the framework | C# against the framework |
| References the framework assembly | No | Yes | Yes |
| Declared multiplayer type | `All` | `All` | The one that matches what the mod changes |
| Example | [data-only-role](../examples/data-only-role/) | [ability-role](../examples/ability-role/) | [utility-mod](../examples/utility-mod/) |

## Data-only role

The whole role is a manifest embedded in the plugin's DLL. The framework discovers the
manifest, registers the tree and perks with the game, and draws the role in the gene-tree
terminal.

It can:

- Declare a role with a name, tooltip, accent colour and role icon.
- Place perks on the tree, each with levels, a target (the character, the manned module, the
  ship while seated, or the ship for every holder) and a list of stat modifiers per level.
- Filter an effect by game tags, grant fabricator unlocks, and suppress stock trees or other
  framework roles.
- Declare custom stats, which stay inert until code reads them.

It cannot run code. The hero perk grants nothing without a registered ability, so a
data-only role marks its hero `emptyOk`. Every effect is a stat modifier the game already
knows how to apply. [manifest.md](manifest.md) lists every field and
[stat-names.md](stat-names.md) lists the stats a modifier can name.

It asks the author for a JSON file, a few icons and a short plugin. The
project does not reference the framework assembly, so a framework update does not change the
DLL's bytes. The manifest's `contractVersion` and optional `minFrameworkVersion` say which
framework it needs; [versioning.md](versioning.md) covers both.

## Role with code

A role with code is a data-only role plus a plugin that calls the framework. The usual
reason is a role ability. The plugin registers a factory that returns an
`EctypeRoleAbility` subclass, and the framework installs it, binds the key and runs the
cooldown and duration. The hero perk selects the ability. Other reasons to add code are a
perk whose effect is a timed status, a HUD button, or loot that only drops while the role is
available in the room.

It can do everything a data-only role can, and anything the framework's utility APIs and the
game's own types allow. The API areas are indexed in [api.md](api.md).

It asks the author for C# written against the framework and the game's assemblies, with the
extra references listed in [setup.md](setup.md). The DLL records the framework assembly
version it was compiled against. After every framework update, rebuild it against the new
framework, raise its version and reship it, because the new reference changes the DLL's bytes
and a mismatch is refused at join. A mod that depends on the framework with a minimum version
in `[BepInDependency]` also refuses to load under an older one.

## Utility-only mod

A utility-only mod has no manifest and no role. It calls the framework's utilities: enemy and
character statuses, loot tables, ship arrivals, timed stat contributions, the HUD ability bar,
framework-owned stats, runtime ability replication and the selection gate. [api.md](api.md)
indexes them.

It asks the author for C# against the framework, with the same rebuild rule as a role with
code.

Its multiplayer type follows from what it changes:

- A mod whose effects only the local player sees, such as a log line or a readout, declares
  `Client`. VoidManager never refuses a join over a `Client` mod, and the mod does not raise
  the modded-session flag.
- A mod that changes what other players experience declares `Session` or `All`. Both raise
  the flag, and `All` also refuses a join when the versions or builds differ. The values and
  their costs are in [multiplayer.md](multiplayer.md).

## What each shape costs players

| | Data-only role | Role with code | Utility-only mod, `Client` | Utility-only mod, `Session` or `All` |
| --- | --- | --- | --- | --- |
| Who installs the framework | Everyone in the room | Everyone in the room | Everyone in the room | Everyone in the room |
| Who installs the mod | Everyone in the room | Everyone in the room | Players who want it | Everyone in the room |
| Same build required | Yes, of the mod and the framework | Yes, of the mod and the framework | Not for the mod; yes for the framework | Yes for `All`. A `Session` mod is not version-matched, so ship it as if it were |
| Marks the session as modded | Yes | Yes | The framework does | Yes |

[multiplayer.md](multiplayer.md) states each fact behind the table in full.

- **The framework declares `All`.** A player who runs any dependent mod runs the framework,
  and the framework refuses a join when the versions or builds differ. A `Client` mod does not
  remove that requirement, so a room still needs the framework on every machine.
- **The framework raises the modded-session flag.** VoidManager sets a process-wide flag when
  a plugin declaring `Session` or `All` loads. A room hosted by a process that loaded the
  framework is marked modded, and the mark cannot be withdrawn until the game restarts. A
  modded session does not count toward progression. A mod that must leave the session
  unflagged cannot depend on the framework.
- **Role content is mandatory for the whole room.** A role's tree and perks register in the
  game's required-asset set, and the game refuses a join when the host's required assets and
  the joiner's differ, whatever the mods declare. A role cannot be optional for some players.

The listing for a mod that declares `All` says all of this in plain words;
[publishing.md](publishing.md) lists the points.

## Picking one

1. Does the idea need code? If every effect is a stat modifier on the character, the manned
   module or the ship, write a data-only role and start from [quickstart.md](quickstart.md).
2. Does it need a role ability or other behaviour, and does it belong to a role? Write a
   role with code. Start from the data-only role and add the factory.
3. Is it behaviour with no role? Write a utility-only mod, and choose its multiplayer type by
   who has to see the effect.
