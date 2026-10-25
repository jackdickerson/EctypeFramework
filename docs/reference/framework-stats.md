# Framework stats

Some numbers the game gives every player are plain fields, not `ModifiableFloat`s. No perk can
move them, no tooltip describes them, and two mods touching one fight each other. The
framework wraps those fields as stats, so a mod contributes through the game's own modifier
composition instead of writing the field.

These stats are registered once per session whether or not any role is installed. Any
manifest can name them without declaring them (see [../stat-names.md](../stat-names.md), rows
with source `framework`), and code builds a `StatMod` against `FrameworkStats.Id(name)` the
same way it would for a vanilla stat. `FrameworkStats` is public. A stat the framework
registers is not scoped to a role, and unlike a role's own `customStats` it already has a
consumer. [../framework-stats.json](../framework-stats.json) describes each stat: what it
wraps, the value the game authored, and how the game consumes it. The file is hand-maintained
documentation that no code reads; its `_fields` object defines each per-stat field, and
`.github/scripts/check-stats.py` checks its names and ids against the declarations in
`src/FrameworkStats.cs` and the framework rows of [../stat-names.csv](../stat-names.csv).

## Field-backed stats

A field-backed stat is a `ModifiableFloat` seeded from the field's current value, registered
into a stat collection, with a change handler that writes the composed value back into the
field. The handler also runs on the collection's `OnModifiersChange`, which is the only event
a dynamic-value modifier raises when its amount moves. The game's code stays untouched, and
the modifier stack, composition and tooltips are all the game's own.

A mod that writes the field raw instead of through the stat is last-writer-wins against any
stat contributor and is not arbitrated with it. A field the game already exposes as a stat is
never written any other way.

## The character's stat surface

Five Opsive locomotion numbers are wired onto the player's own collection at spawn, on the
local player and on every replica:

| Stat | Moves |
| --- | --- |
| `ExternalForceDamping` | jetpack and EVA momentum decay |
| `ExternalForceDampingTime` | jetpack and EVA momentum decay |
| `MotorDamping` | walking top speed |
| `MotorAcceleration` | walking top speed |
| `GravityAmount` | gravity-field descent rate |

A perk with target `character` reaches them.

## Install speed

`SystemModuleConstructionSpeed` is a factor (authored 1) on the player's own collection. It sets how fast
that player installs, uninstalls and upgrades ship modules.

The master runs every build process against one room-wide duration. A `BuildProcess.Run`
prefix advances the clock of an install, uninstall or upgrade by `deltaTime * (factor - 1)`
through its working phase and the 3.25 s fold after it. The factor is read from the process's
`RequesterActorId`, which is whoever socketed the build box, pressed uninstall or used the
upgrader, not whoever holds the perk. The 1 s placement check is untouched. Two client-side
visuals keep stock speed: the build box's progress diodes (short of full when the module
appears) and its fold animation (an install's box is destroyed at 3.25 s / factor, cutting it
short).

## The grappling hook's stat surface

Flight speed, pull speed, pull acceleration and the miss cooldown are plain floats in vanilla,
so the stock perks that claim to upgrade the hook carry no effects at all. The framework makes
each a field-backed stat on the hook's own collection. They are registered per hook in an
`Awake` postfix, which covers every hook, including a subclass from another mod and remote
players' hooks. The hook's grapple cooldown also gets its own scoped id, so a cooldown perk on
it cannot leak into other abilities.

## The ship's stat surface

Both stats live on each player ship's own collection, so `seatShip` and `characterShip` perks
reach them (see [roles.md](roles.md)).

- **`IncomingProjectileSpeed`** is a factor (authored 1) on each ship. As a hostile NPC shot
  registers, the framework picks one ship for it and puts that ship's factor on the shot's own
  `Speed`. It re-applies the factor to that ship's shots in flight when the factor changes.
  With one ship the pick is that ship. With several it is the guided shot's target ship, else
  the hostile ship with the smallest angle between the shot's direction and the line to the
  ship, ties going to the nearer one. A shot fired by a player ship is never slowed. Every
  client simulates NPC shots and applies each seated player's `seatShip` mods to the seat's
  own ship, and the pick uses only what every client sees at registration, so each shot
  carries the same factor on every client.
- **`ShipDrag`** is the platform's quadratic drag, `MovingSpacePlatform.PhysicalData.Friction`
  (authored 0.7), as a field-backed stat. A factor f scales top speed on every axis by
  1/sqrt(f). Every machine's platform reads it each fixed tick and stat changes are not
  replicated, so apply a contribution on every machine. Cruise control reads it when it
  engages. Signature and auto-tilt latch it at spawn. [ship-speed-surge.md](ship-speed-surge.md)
  is built on it.

## The hand attractor's stat surface

`HandAttractorMaxRange` and `HandAttractorPullVelocity` are the range and pull speed of a
role ability's hand-held attractor. `HandAttractorStats.Register` registers them into that
ability's own collection.

**The mirror.** The same call registers each float a second time under the vanilla
`AttractorMaxRange` / `AttractorPullVelocity` id. The collection matches a `StatMod` against
every key of its stat dictionary, so every character-side modifier on the vanilla id (with its
conditions and tag filters) lands on the hand float through the game's own apply and
remove-by-source. Nothing is copied, so nothing can leak or apply twice from one modifier.

**Rule:** a source names one id, never both. Naming both on one level compounds on the hand
float.

**Reach.** Neither id on a character-side source reaches the ship's attractor, which is a
module in the ship's tree. Sources on the ship's collection (`seatShip` and `characterShip`
perks, ship-installed items) are a different tree and do not reach the hand.

**Timing and count.** `HandAttractorStats.RegisterTiming` adds three more.
`HandAttractorDuration` and `HandAttractorCooldown` are second ids onto the ability's own
`FiniteTimeDuration` and `CooldownSeconds`, the objects the game already registers as
`ClassAbilityDuration` / `ClassAbilityCooldown` and reads at start and in `GoOnCooldown`,
scoped to the hand. `HandAttractorBeams` is a count the ability reads, rounded, at each start.
Name the hand id or the vanilla alias, never both.

Sample: [examples/framework-stats](../../examples/framework-stats/).
