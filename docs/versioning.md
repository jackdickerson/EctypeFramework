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

## Every player needs the same build

The framework declares multiplayer type `All`, and so does every role mod. Two checks apply
to a mod of that type when a player joins a room.

1. The joining client compares its mod list with the host's list. Mod name, GUID, version and
   multiplayer type are visible to every peer. A mod missing on either side, or present with a
   different version, makes the joiner disconnect itself.
2. The host compares a SHA-256 hash of each mod's DLL with the joiner's. The hash is held by the
   host only, so this check runs only there, and a mismatch kicks the joiner. Equal version
   numbers do not help: the hash covers the whole file, including every embedded manifest and
   icon.

Host migration changes who runs the second check. The framework re-sends each client's hashed
list to the new host so the check keeps working for members who were already present.

### A framework update rebuilds every dependent

A compiled mod records the assembly version of each assembly it references. A dependent built
against framework `0.19.7` carries a reference to `EctypeFramework, Version=0.19.7.0`. Building
the same source against framework `0.20.0` writes a reference to `0.20.0.0`, so the dependent's
bytes change and so does its hash, with no change to its own code or its own version.

The consequences for the author of a dependent:

1. After a framework update, rebuild the dependent against the new framework DLL.
2. Raise the dependent's own version. Two builds of the same version number that differ in
   bytes are bounced by the host's hash check, and the version number is what tells a player
   which copy is stale.
3. Reship the dependent, and tell players to update the framework and the dependent
   together. The Thunderstore dependency string names the framework version the dependent
   was built against, so a mod manager installs the pair as one set.
