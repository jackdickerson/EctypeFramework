# Versioning

How the framework is numbered, what two numbers in a role manifest gate, and what a
framework update costs a dependent mod.

## The version number

The framework has one version, `x.y.z`, set by the `PLUGIN_VERSION` constant in its source.
The plugin version, the assembly version (`x.y.z.0`), the file version and the Thunderstore
package version are all derived from it.

| Part | Rises when |
| --- | --- |
| `z` | Every change between Thunderstore releases. Builds with `z > 0` are development builds handed to testers. |
| `y` | A Thunderstore release. `z` returns to 0. Public API added or behaviour changed since the previous release ships here. |
| `x` | A release that removes public API. `y` and `z` return to 0. The framework is `0.y.z` while in beta and reaches `1.0.0` when it is public. |

Every change to the framework gets a new number.

Two builds with the same number are the same build. A build is never redistributed under a
number that has been handed out.

The changelog lists the versions that changed something a dependent can observe. A version
missing from it carried no such change.

## What a dependent can rely on

- **The public API changes by version part.** A supported public type or member is removed
  only in a release that raises `x`, to `x.0.0`. A release that raises `y` may add members
  and change behaviour. Development builds (`z > 0`) carry the changes of the release they
  lead to. The supported types are the ones listed in [api.md](api.md).
- **A build compiled against version V needs V or newer installed.** The compiled mod
  names the types and members it calls. A framework without one of them fails at the first
  call that touches it, with a `TypeLoadException` (or the matching missing-member
  exception) raised inside the mod's own code. List the version you built against in the Thunderstore
  dependency string, and in the manifest as `minFrameworkVersion` if the role needs a
  feature added after the first release.
- **The manifest format is versioned separately.** `contractVersion` changes only when the
  manifest's meaning changes in a way an older reader would misread. Version numbers of the
  framework and of the contract are independent.
- **A manifest field the framework does not know is ignored.** A manifest never fails
  because it names a field from a newer contract. What it asks for is lost silently, which
  is the reason to stamp `minFrameworkVersion` when the role uses a newer feature.
- **A rejected role is absent, never half-registered.** Every rejection below skips that
  role only. The terminal, the other roles and the rest of the game load normally.

## What `contractVersion` and `minFrameworkVersion` gate

The framework checks both when it reads a role mod's embedded manifest, before it
registers anything from it.

**`contractVersion`** is required. The framework compares it for equality with the contract
version it implements, which is currently `2`. Any other value rejects the role, whether
higher or lower. The log line is

```
Ectype: manifest '<resource name>' in <assembly> rejected: contractVersion <n> (this framework speaks 2)
```

**`minFrameworkVersion`** is optional, a string `"major.minor.patch"`. Absent means no
minimum. The framework parses it and compares it with its own version. A framework older
than the value rejects the role:

```
Ectype: manifest '<resource name>' in <assembly> rejected: needs Ectype Framework <min> or newer (this is <installed>)
```

A value that does not parse as a version is also a rejection:

```
Ectype: manifest '<resource name>' in <assembly> rejected: minFrameworkVersion '<value>' is not a version (expected "major.minor.patch")
```

`minFrameworkVersion` is checked first. A manifest that fails both reports the version
requirement.

Set `minFrameworkVersion` to the oldest framework that has every feature the manifest and
the role code use. A role that needs nothing newer than the first release leaves it out.

## The network contract

Framework clients talk to each other on one Photon event, whose payload is a subtype byte
followed by that message's fields. The network contract is that event code, the set of
subtypes and each message's field layout, numbered by the framework's `NET_CONTRACT_VERSION`
constant. The field layouts are declared in one table that the sender and the receiver both
check every message against. The number is independent of the manifest `contractVersion`
above.

Raise `NET_CONTRACT_VERSION` in the same change that adds, removes or renumbers a subtype or
changes a message's fields, and name the new value in that version's `CHANGELOG.md` entry as
`network contract <n>`. The release check fails when the changelog does not name the current
value.

Two things follow from the number:

- **Peers filter on it.** Each framework publishes it as the Photon player property `Ectype`,
  and messages go only to peers whose property matches.
- **The game refuses a mismatch at join.** The framework registers one runtime asset whose
  GUID ends in the contract number, inside the game's Required mask. The game's own asset
  check compares the host's and the joiner's registered assets, so a joiner on another
  contract is refused, and the rejection screen shows the asset's name,
  `Ectype Framework <version> (network contract <n>)`, for the host's framework. This check
  runs with or without VoidManager.

## Every player needs the same build

A mod that declares a `VoidPlugin` of multiplayer type `All` gets two VoidManager checks
when a player joins a room, provided the host and the joiner both run VoidManager. The
framework declares no VoidManager type, so neither check covers the framework itself: its join
check is the network contract above, and its version is matched by the mod launcher alone.
A role mod that follows the examples declares no `VoidPlugin`, so neither check applies to it.

1. The joining client compares its mod list with the host's list. Mod name, GUID, version and
   multiplayer type are visible to every peer. A mod missing on either side, or present with a
   different version, makes the joiner disconnect itself.
2. The host compares a SHA-256 hash of each mod's DLL with the joiner's. The hash is held by the
   host only, so this check runs only there, and a mismatch kicks the joiner. Equal version
   numbers do not help: the hash covers the whole file, including every embedded manifest and
   icon.

The Void Crew Mod Launcher installs the host's exact Thunderstore versions on a joiner's machine
before the game starts. A Thunderstore version number is uploaded once, so the same package
version is the same bytes everywhere and both checks pass. A build that is not on Thunderstore
cannot reach a joiner that way.

### Rebuild only to use new API

A compiled mod records the assembly version of each assembly it references. A dependent built
against framework `0.19.7` carries a reference to `EctypeFramework, Version=0.19.7.0`.
`EctypeFramework.dll` is not strong-named, and the game's Mono runtime binds a reference to a
non-strong-named assembly by simple name, so that dependent loads and runs against a newer
framework as long as the members it calls still exist. This is verified by mechanism and not
yet by a test in game.

The consequences for the author of a dependent:

1. Rebuild against a new framework only to use API that framework added.
2. A framework update does not by itself require reshipping dependents. The host's hash check
   compares each DLL with the same DLL on the joiner's machine, so a dependent's bytes change
   only when its author rebuilds it. Players still update the framework itself to the host's
   version.
3. Publish every build you give to anyone to Thunderstore, under a new version. Thunderstore
   refuses a version number it already has, so a new build forces a new number, and the mod
   launcher can then install it for a joiner. A build handed out any other way cannot be
   downloaded by a joiner, and two builds under one number are kicked by the host's hash check.
4. Name the framework version you built against, or newer, in the Thunderstore dependency
   string, so a mod manager installs a framework that has every member your build calls.
