# Multiplayer

What every player in a room needs when a mod depends on EctypeFramework, how a room's mod set
is matched, how the game and VoidManager refuse a join, and what a dependent mod has to tell
its players. The VoidManager behaviour described here is that of VoidManager 1.2.11.

## The short version

- Every player in a room runs the framework and every role mod any player has, at the same
  Thunderstore version.
- A role mod declares `MultiplayerType.All`. The framework declares it too.
- The Void Crew Mod Launcher matches a joiner's mods to the host's before the game starts.
  VoidManager's join check refuses anyone whose set still differs, and the game's asset check
  refuses anyone whose registered role content differs.
- Both join checks run at join. A member already in the room is never re-checked, and the
  framework adds no check of its own.
- Hosting a room with the framework installed marks the session as modded. The mark cannot be
  withdrawn from that room, and a modded session does not count toward progression.

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

- **Declare `MultiplayerType.All`.** The framework and every role mod do, so the launcher
  advertises them as required for everyone in the room.
- **The join check still applies without the launcher.** A joiner who installed mods by hand
  is refused by VoidManager's `All` check when any version differs from the host's.
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
browser. VoidManager's progression handler blocks cloud profile writes and Steam stats and
achievements for a tagged session.

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

- The framework declares `All`, so any room hosted by a process that loaded it is tagged, even
  with no role mod installed and no role selected. A dependent cannot defer the flag.
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
- Hosting with the mod installed marks the session as modded and turns off progression for it,
  and the mark lasts until the game restarts. Disabling the mod in its config does not undo it.
- A player who has the mod cannot join a host who lacks it. The asset check is symmetric.
