# Publishing a dependent mod on Thunderstore

How to package a role mod, or any mod that calls the framework, as a Thunderstore package
for the Void Crew community, and what the listing has to tell players.

## The dependency string

Thunderstore names a package `Author-Package-Version`, where Author is the team namespace
and Package is the package name. A dependency is written in the same form in the
`dependencies` array of the package's `manifest.json`.

The framework's string, as a single value to copy:

```
Jack_Modding-Ectype_Framework-<version>
```

`<version>` is the framework version your mod was built against, written exactly as the
Thunderstore listing shows it (for example `0.19.7`). Use the
version you built against: a mod compiled against one version needs that version or newer,
and every player in a room runs the same build, which the mod launcher installs for a joiner
(see `versioning.md`).

Every package also depends on BepInEx, the one package the framework itself needs:

```
BepInEx-BepInExPack-5.4.2305
```

VoidManager is not a dependency of the framework or of a mod built on it. A mod that declares
a `VoidPlugin` cannot load without VoidManager, so it lists
`NihilityShift-VoidManager-1.2.11` as well. A mod that uses VoidManager only on a soft
dependency, for an F5 page, runs without it and leaves it out, so the mod manager does not
install VoidManager for players who did not choose it.

Check each version on thunderstore.io/c/void-crew before every upload. Packages are updated
independently of your mod.

## Package layout

A Thunderstore package is a zip with these files at the archive root, with no enclosing
folder:

| File | Content |
| --- | --- |
| `manifest.json` | `name`, `version_number`, `website_url`, `description`, `dependencies` |
| `README.md` | The listing page |
| `CHANGELOG.md` | The listing's Changelog tab |
| `icon.png` | Exactly 256 by 256 pixels |
| `<YourMod>.dll` | The built plugin |

Rules the upload enforces:

- `name` contains only letters, digits and underscores. Spaces in a display name become
  underscores.
- `version_number` is `major.minor.patch`. A version number can never be uploaded twice, so
  every upload needs a new one.
- `description` is at most 250 characters.
- Text files are UTF-8 without a byte order mark. A README with a BOM is rejected.

A minimal `manifest.json`:

```json
{
  "name": "My_Role",
  "version_number": "0.1.0",
  "website_url": "",
  "description": "One sentence that says what the role is.",
  "dependencies": [
    "BepInEx-BepInExPack-5.4.2305",
    "Jack_Modding-Ectype_Framework-<version>"
  ]
}
```

Keep the mod's own `PLUGIN_VERSION` and the package's `version_number` identical. The mod
launcher and a player reading the listing go by the package version; BepInEx logs the plugin
version, and VoidManager compares it across the room for a mod declared `All` to it. Raise both
on every build you give to anyone: a joiner can download only what is on Thunderstore, and two
builds under one version number cannot be told apart by anything but their bytes.

## Where the files land

A mod manager (Gale, r2modman, Thunderstore Mod Manager) unpacks the package into the game's
`BepInEx\plugins\` tree and installs the dependencies listed in `manifest.json` beside it.
A player installing by hand drops the DLL into `Void Crew\BepInEx\plugins\`.

A role mod's icons and manifest are embedded in the DLL, so the DLL is the only file the
package carries besides the listing files. Nothing needs to be unpacked anywhere else.

The framework DLL is installed from its own package. Do not put a copy in yours: two copies
register the same plugin GUID.

## What the listing must say

Players decide from the listing whether they can join a room, so it states these points in
plain words.

1. **Everyone in the room needs it.** A role mod's content and the framework's contract
   marker are registered in the game's required-asset set. A player without them cannot join
   a room that has them, and a host without them bounces a joiner who has them.
2. **Everyone needs the same version.** What enforces it at join:
   - The game's asset check, always: it refuses a joiner whose framework speaks another
     network contract, and one whose role content (trees, perks) differs from the host's. It
     does not compare version numbers.
   - VoidManager, when the host and the joiner both run it: it refuses a joiner whose version
     of a mod declared `All` differs from the host's, and the host also compares that DLL's
     bytes. The framework declares no VoidManager type, so this never covers the framework
     itself.
   - The Void Crew Mod Launcher, before the game starts: it installs the host's exact
     versions, the framework included.

   Two framework versions on the same network contract can therefore share a room when
   players install by hand. A Thunderstore version number is uploaded once, so the same
   package version is the same bytes on every machine. State the version the listing was
   written for and tell players to update the framework and your mod together.
3. **Sessions are flagged as modded.** The game tags a room as modded and shows the tag in
   the server browser, which is how it separates modded rooms in matchmaking. The tag is
   set when the mod loads and cannot be switched off from the mod's settings. The framework
   does not touch progression, achievements or cloud saves; with VoidManager installed,
   VoidManager's own policy applies to mods that declare `All` or `Session` to it.
4. **What the framework dependency means.** One line: the role needs the Ectype Framework
   package, which the mod manager installs automatically and which a hand installer has
   to add.

The framework's own package carries the Thunderstore category `All Required`, written into the DLL's
`AssemblyMetadata("ModCategories", "Mods, All Required")`, from which the publish script takes the
package's categories; a build with `-p:NoProgressionFlag=true` registers nothing in the game's
required set and carries `Mods` alone. It declares no
`VoidPlugin`, so a mod launcher that looks for one in the DLL finds none and falls back to the
package's categories; the DLL also carries `AssemblyMetadata("MPType", "All")` for a reader
that inspects assembly metadata.

A mod that declares no `VoidPlugin` tells the Void Crew Mod Launcher through the same
categories who in a room needs it. The launcher reads them in this order, first match wins:

| Category | The launcher reads it as |
| --- | --- |
| `All Required` | every player: installed for each joiner at the host's version |
| `Client-Side`, without `Host-Side` | the players who want it; not installed for joiners |
| `Host-Side`, without `Client-Side` | the host only; not installed for joiners |
| `Mod_Session Required`, without `Client-Side` | needs a modded room, which the framework already makes; not installed for joiners |
| `Cosmetics` or `Decorations` | the players who want it; not installed for joiners |
| none of these | unknown, and installed for joiners |

A role mod carries `All Required`. A mod that declares a `VoidPlugin` with
`MultiplayerType.All` is read as every player whatever its categories say, and the launcher
then installs VoidManager for each joiner too.

The community also has `Disables Progression`; the framework does not disable progression, so
a dependent mod carries that category only if its own code does. The list changes over time:
read the live list (`https://thunderstore.io/api/experimental/community/void-crew/category/`)
before you set categories.

A listing skeleton for the multiplayer section:

```markdown
### Multiplayer

- Everyone in the room needs this mod and the Ectype Framework, at the same version.
- Hosting a room with this mod marks the session as modded in the server browser until the
  game restarts.
- Players without VoidManager can join only a host that either has no VoidManager or runs no
  mod declared `All` to it.
- Updating: update this mod and the Ectype Framework together.
```

## Uploading

Upload the zip from the team's page on thunderstore.io, or with the Thunderstore CLI:

```
tcli publish --file <package>.zip --config-path <config>.toml --token <api token>
```

The config file names the team namespace, the package name, the version, the community
(`void-crew`) and the category slugs. The API token is created under the team's service
accounts.
