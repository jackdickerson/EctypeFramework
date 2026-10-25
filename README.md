# EctypeFramework

A framework for Void Crew mods, in two halves that ship as one assembly.

- **The role framework.** A role in Void Crew is a perk tree whose row-0 perk has display
  type `Hero`. The game's screens assume exactly four trees. The framework patches those
  surfaces once per process, so a role mod carries only content: a JSON manifest, icons and
  optionally a role ability.
- **General utilities** for any mod: mutator safety, timed enemy and character statuses,
  field-backed stats, runtime ability replication, ship arrivals from the void, curve-driven
  stat contributions, loot-table registration, a HUD ability bar, how-to toasts through the
  game's hint system and a room build check.

## For players

A dependency: install it when another mod lists it as required. On its own it adds no
gameplay, and it keeps clients from crashing when a run carries a mutator whose mod they
lack. Everyone in a room needs the same build of this mod and of every mod that depends on
it; a mismatched version bounces the joiner.

## For modders

- [Quickstart](docs/quickstart.md): from install to a role visible in game.
- [Setup](docs/setup.md): building a dependent mod on a machine that has only the game.
- [Choosing a mod shape](docs/choosing.md): a data-only role, a role with code, or a
  utility-only mod.
- [Role manifest reference](docs/manifest.md) and [stat names](docs/stat-names.md).
- [Multiplayer](docs/multiplayer.md): what every player in a room needs.
- [Troubleshooting](docs/troubleshooting.md): each message the framework logs.
- [Versioning](docs/versioning.md) and [publishing](docs/publishing.md).
- [API index](docs/api.md) and the [changelog](CHANGELOG.md).
- [Examples](examples/): buildable example mods.

### Reference

- [Role mods](docs/reference/roles.md)
- [Configuration](docs/reference/config.md)
- [Session data and host-synced settings](docs/reference/session-data.md)
- [Mutator safety](docs/reference/mutator-safety.md)
- [Loot tables](docs/reference/loot-tables.md)
- [Enemy statuses](docs/reference/enemy-statuses.md)
- [Enemy abilities](docs/reference/enemy-abilities.md)
- [Character statuses](docs/reference/character-statuses.md)
- [Ship arrivals](docs/reference/ship-arrivals.md)
- [Topology: which ship is whose](docs/reference/topology.md)
- [Interaction gate](docs/reference/interaction-gate.md)
- [Timed stat contributions](docs/reference/timed-stat-contributions.md)
- [Ship speed surge](docs/reference/ship-speed-surge.md)
- [HUD ability bar](docs/reference/hud-ability-bar.md)
- [Framework stats](docs/reference/framework-stats.md)
- [Mod-declared stats and the session collection](docs/reference/mod-stats.md)
- [Runtime ability replication](docs/reference/runtime-abilities.md)

The source code is licensed under the MIT licence: see [LICENSE](LICENSE).
