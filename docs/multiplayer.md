# Multiplayer

What every player in a room needs when a mod depends on EctypeFramework, how a room's mod set
is matched, how the game and VoidManager refuse a join, what a dependent mod has to tell its
players, and what changes in a room with more than one player ship. The framework runs with or
without VoidManager. VoidManager's join check and progression switch act only for the players
who run it; where this page describes them, it describes VoidManager 1.2.11.

## The short version

- Every player in a room runs the framework and every role mod any player has, at the same
  Thunderstore version.
- A role mod declares `MultiplayerType.All`. The framework declares no VoidManager type, so
  VoidManager never refuses a join on its account; see "Players with and without
  VoidManager".
- The Void Crew Mod Launcher matches a joiner's mods to the host's before the game starts.
  VoidManager's join check refuses anyone whose set still differs, and the game's asset check
  refuses anyone whose registered role content differs.
- Both join checks run at join. A member already in the room is never re-checked. The
  framework's own contribution is one asset the game's check compares, below.
- Hosting a room with the framework installed marks the session as modded. The mark cannot be
  withdrawn from that room. The framework keeps the session out of progression and
  achievements until the game restarts, with or without VoidManager; see "The modded-session
  flag and progression".

## Matching mods with the launcher

The Void Crew Mod Launcher (https://github.com/CaptainChunck/VoidCrewModLauncher) matches a
room's mods outside the game:

1. The host enables mods in the launcher, then launches the game.
2. The host's lobby advertises the Thunderstore id and exact version of each enabled mod.
3. A joiner's launcher installs those exact versions and restarts the game with that set.

Thunderstore accepts each version number of a package once, so the same package version is
the same DLL bytes on every machine. A joiner who arrives through the launcher holds the
host's builds and passes both join checks.

What follows for a mod author:

- **Make the launcher advertise the mod as required for everyone.** It reads a mod's
  VoidManager type when the mod declares one (`All` means everyone) and the package's
  Thunderstore categories otherwise (`All Required`). The framework declares no VoidManager
  type and carries `All Required`.
- **The join checks still apply without the launcher.** A joiner who installed mods by hand
  is refused by VoidManager's `All` check when any version of an `All` mod differs from the
  host's (both sides running VoidManager), and by the game's asset check when role content or
  the framework's network contract differs.
- **Ship through Thunderstore and raise the version on every build.** The launcher can install
  only what is published. A DLL you built locally and handed to a friend cannot be downloaded
  by anyone else who joins, and two different builds under one version number are refused by
  the host's byte comparison below.

The check: a joiner who differs disconnects with `Incompatable mod list`, or is removed by the
host shortly after arriving.

## The two join checks

### VoidManager's mod-list check

VoidManager publishes each player's mod list: for every plugin that declares a `VoidPlugin`,
its name, GUID, version and `MultiplayerType`. A plugin declared `Hidden` is left out of the
list entirely.

The check runs twice:

1. **On the joining client, before it enters.** The client reads the host's list from the
   room properties and diffs it against its own in both directions. On any failure it
   disconnects itself with `Disconnected: Incompatable mod list` (VoidManager's spelling). A
   mod that only the client has is reported as `Host is missing the required mod`; a mod that
   only the host has, as `Client is missing the required mod`.
2. **On the host, when the joiner arrives.** The host matches the joiner's entries by GUID,
   compares `Version`, then compares a SHA-256 hash of each mod's DLL file, and kicks on a
   failure. The hash covers the whole assembly, embedded manifests and icons included, so this
   is the check that catches two different builds carrying the same version number.

Six `MultiplayerType` values exist and two of them can refuse a join:

| Type | Refuses a join when |
| --- | --- |
| `All` | the versions differ, the DLL bytes differ (host-side only), or either side lacks the mod |
| `Session` | the joiner declares it and the room is not already a modded session |
| `Client`, `Host`, `Unmanaged` | never |
| `Hidden` | never, and the mod is invisible to every peer |

A `Session` mod is not version-matched: a stale copy joins and desyncs silently, so ship it to
the whole room as if it were `All`.

### The game's asset check

`GameSessionManager.ValidateMods`, called from `JoinGameSession`, compares registered
assets and knows nothing about mods. It collects the assets the host's session holds that the
joiner lacks, and the assets the joiner holds that the host lacks. Either list refuses the
join. Both sides read the registered assets whose `SessionModificationEffect` falls inside the
`Required` mask (value 94), so the comparison is symmetric: a client with required content
cannot join a host that lacks it, and the reverse.

The rejection screen names individual assets. VoidManager's screens name mods. That is how
the two checks are told apart in a bug report.

The framework registers one such asset itself: a marker whose GUID ends in its network
contract number ([versioning.md](versioning.md)). Two frameworks that cannot exchange messages
therefore cannot share a room, with or without VoidManager, and the screen names the host's
framework as `Ectype Framework <version> (network contract <n>)`. Being in the `Required`
mask, the marker also tags every room the process hosts as modded (route 1 below), which is
what tags a room when VoidManager is not installed.

## Players with and without VoidManager

The framework runs with or without VoidManager, and so can a room:

- **A host without VoidManager** publishes no `Mods` room property, only the game's modded
  tag. VoidManager's client-side check on a joiner reads the host's mod list whenever the room
  is tagged, and fails without one. On a joiner that runs the framework, the framework passes
  that check for a tagged room with no `Mods` property, and the game's asset check decides.
  A joiner with VoidManager and without the framework cannot join anyway: it lacks the
  framework's contract marker.
- **A host with VoidManager** kicks a joiner who has no `Mods` player property, that is a
  joiner without VoidManager, whenever the host's highest declared VoidManager type is `All`.
  It does the same while the host has turned VoidManager's progression switch off. This is
  VoidManager's own policy. The framework declares no type, so it never causes the kick, but
  any `All` mod on the host does, role mods included.

A room that must take players without VoidManager therefore needs a host without VoidManager,
or a host whose mods declare nothing above `Session` to VoidManager. The framework's own
compatibility rests on the game's asset check in every case: its contract marker and the role
mods' required assets.

## Why role content is mandatory for the room

The framework registers each role's gene tree and every perk inside the `Required` mask.
Three facts bear on a mixed room:

- The asset check runs at join, before the room's members are known and before any mod could
  negotiate. It cannot compute "the roles everyone shares".
- The check reads the asset registry. Hiding a role leaves its registry entries in place, and
  manifest suppression (`suppressRoles`) does exactly that. Only unregistering removes an
  asset, and nothing unregisters during a process.
- A client that lacks a role's perk GUIDs cannot apply another player's perks. The game's
  perk reapply throws on an unknown GUID and leaves that player's perk set empty on the
  client that threw. Only a process running the framework's guard avoids it.

A role mod therefore declares `All`, depends on the framework with `[BepInDependency]`, and
is installed by everyone in the room. A design that relies on players holding different role
sets is refused by the game regardless of the `MultiplayerType` a mod declares.

## Suppression in a room

A role mod can remove stock trees with its manifest's `suppress` list and other framework
roles with `suppressRoles` ([manifest.md](manifest.md)). Both are manifest data, so every
client with the same mod set derives the same result, and nothing changes during a session.

`RoleRegistry.IsAvailableInRoom(roleId)` is true while a role is registered and no other
active role suppresses it. Gate role-owned content (loot, spawns) on it.

A suppressed role stays registered. Every player still needs it installed, its perks stay in
each saved loadout and are read as absent, and its spent points count as unspent.

## The modded-session flag and progression

The game tags a room as modded with the room property `R_Mod`. The tag shows in the server
browser. The game itself reads it for nothing else.

The framework keeps a modded session out of progression, with or without VoidManager. The
first time the client is in a room that carries the tag, the guard engages and stays engaged
until the game restarts. The profile in memory already holds what that session earned, so
progression is never written again in that process. Every room hosted by a process with the
framework carries the tag (route 1 below), so in practice the guard engages in the player's own
hub room, before any mission.

The game saves the cloud profile as separate entries, each under its own key, through one write
method. While the guard is engaged:

| Not saved | Still saved |
| --- | --- |
| Profile data: level and experience, tokens, quest progress, the challenge-run seed, and the fields saved with them (seen hints, the awakening flag, accepted EULA and GDPR versions) | The block list |
| Unlocked items | The cosmetic loadout |
| Achievements completed and claimed, and achievement metrics | Ship loadout presets |
| Metaquest completion times and pending rewards | Gene-tree loadouts and which one is active |
| The preserved (resumable) run | The "new item" badges, saved on exit |
| The offline profile's save on exit | |
| Steam stats and achievements | |

A key the game adds in a later version is not saved until the framework lists it. Gene-tree
loadouts are a choice of perks, not progression: perk points come from the player's level,
which is not saved. A loadout saved with a framework role's perk loads in a game without that
role mod with the perk absent and its points unspent, because the game's loadout reader drops
a perk it cannot resolve. With `[Debug] VerboseLogging` on, each skipped key is logged once.

VoidManager's own progression switch does not react to the tag: it turns off only when a mod
calls its `ProgressionHandler.DisableProgression`, or the host has done so. When it is off it
skips every cloud write, settings-like keys included.

A mod sets the tag by any of three routes:

1. **Registering an asset in the `Required` mask.** The host publishes the tag whenever the
   registry holds any required asset, whether or not the feature is ever used.
2. **Declaring `Session` or `All`.** VoidManager raises a process-wide static,
   `SessionModdingType`, when such a plugin loads. The host publishes the tag from it for every
   room the process hosts afterwards. Declaring `Client` or `Hidden` does not raise it.
3. **Returning `SetMod_Session = true` from `OnSessionChange`.** VoidManager calls it on room
   creation, on session start and on join.

The tag is one-way. A room that has it keeps it, and `SessionModdingType` is never reset
during a process, so rooms hosted later in the same play session carry it too. Only a full
game restart clears it. A mod's own `Enabled` config key does not un-flag a session: for a
`Session` or `All` mod the static is raised at load, whatever the config says.

Consequences for a dependent of the framework:

- The framework's contract marker sits in the `Required` mask, so any room hosted by a process
  that loaded the framework is tagged by route 1, with or without VoidManager, even with no
  role mod installed and no role selected. A dependent cannot defer the flag.
- A role mod's own assets sit in the `Required` mask, so route 1 applies as well.
- A mod that must stay silent until its content is used cannot depend on the framework.

## What your mod's page must tell players

For a mod that declares `All`:

- The framework is required, by its name and Thunderstore dependency, and every player in the
  room installs it.
- Every player in the room installs your mod at the host's version. Players who use the Void
  Crew Mod Launcher get the host's versions installed for them on join; anyone installing by
  hand matches the version and is refused at join otherwise.
- When you release an update, every player updates together, including the framework if you
  rebuilt against a new one.
- Hosting with the mod installed marks the session as modded, and the mark lasts until the
  game restarts. Disabling the mod in its config does not undo it. The framework then blocks
  progression and achievements on every client in the room until the game restarts.
- A player who has the mod cannot join a host who lacks it. The asset check is symmetric.

## Rooms with more than one ship

A mod can put several player ships in one room, for co-op crews or for teams playing against
each other. The game was written for one ship and reads it through one getter. Such a mod can
make that getter mean "the ship this client is viewing", which differs between clients and
changes as a player walks between ships or a spectator switches view.

Anything a mod changes on every client must therefore resolve each player's ship from data
every client shares, never from that getter. The framework does this itself and offers the same
resolution to dependents as [`Topology`](reference/topology.md): the list of living ships, each
player's crew ship, the ship a character stands in, the ship a part belongs to, and whether two
things are on opposing sides. With one ship in the room every answer is that ship, so nothing
changes there.

What the framework's own features do with several ships:

- **`characterShip` perks** land on each holder's own crew ship, resolved per holder on every
  client. A holder with no crew ship yet (spectating, not placed, ship dying) contributes
  nothing until one exists. Twice a second, and whenever a ship spawns or dies, each holder is
  resolved again, and only a holder whose ship changed is rebuilt. A player boarding an enemy
  ship never buffs it, and when one side's ship is the last one left, the other side's players
  contribute nothing to it.
- **`seatShip` perks** land on the ship of the chair the player sits in. The game applies them
  from the chair's own events.
- **`IncomingProjectileSpeed`** is per ship. Each hostile shot takes the factor of one ship,
  chosen as it registers from what every client sees at that moment: the guided shot's target
  ship, else the hostile ship closest to the shot's line. A shot fired by a player ship is never
  slowed.
- **`ShipArrivals.Arrived`** fires once per ship per jump, each time naming that ship.
  `ShipArrivals.FleetArrived` fires once per jump, after the last ship is out of the void
  ([ship-arrivals.md](reference/ship-arrivals.md)).
- **Status markers** follow the ship this client views for target-lock gating, and take their
  colour from the game's own affiliation rule, which a team mod can make side-relative. Past
  the marker cap, the markers nearest the viewed ship are the ones kept.
- **Enemy statuses** judge who applied them. A status declared `Hostile` or `Friendly` is
  refused on every client when the sender is on the wrong side of the target, a status is
  removed only by its applier, the target's owner or the master, and `Visibility` limits its
  marker to one side's players
  ([enemy-statuses.md](reference/enemy-statuses.md#who-may-apply-and-remove)).
- **Enemy ability suspension** lifts the enemy's modifiers from every player ship.

[topology.md](reference/topology.md) lists the rules a dependent follows in such a room: never
read the player-ship getter for another player, spawn through the game's own paths, keep live
objects out of dynamic stat rules, do not graft components another mod indexes, and register
crafting data for every carryable.
