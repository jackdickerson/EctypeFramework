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
and the room check requires every player to run the same build (see `versioning.md`).

Every package also depends on the two packages the framework itself needs:

```
BepInEx-BepInExPack-5.4.2305
NihilityShift-VoidManager-1.2.11
```

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
    "NihilityShift-VoidManager-1.2.11",
    "Jack_Modding-Ectype_Framework-<version>"
  ]
}
```

Keep the mod's own `PLUGIN_VERSION` and the package's `version_number` identical. VoidManager
compares the plugin's version across the room, while the mod launcher and a player reading the
listing go by the package version. Raise both on every build you give to anyone: a joiner can
download only what is on Thunderstore, and the host refuses a joiner whose DLL differs under
the same version number.

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

1. **Everyone in the room needs it.** A role mod and the framework are multiplayer type
   `All`. A player without them cannot join a room that has them, and a host without them
   bounces a joiner who has them.
2. **Everyone needs the same version.** VoidManager refuses a joiner whose version differs
   from the host's, and the host also compares the DLL's bytes. A Thunderstore version number
   is uploaded once, so the same package version is the same bytes on every machine. Players
   who use the Void Crew Mod Launcher get the host's versions installed for them when they
   join; everyone else updates by hand. State the version the listing was written for and
   tell players to update the framework and your mod together.
3. **Sessions are flagged as modded.** The game tags a room as modded and shows the tag in
   the server browser, which is how it separates modded rooms in matchmaking. VoidManager
   also blocks cloud profile writes and Steam stats and achievements for a tagged session.
   The tag is set when the mod loads and cannot be switched off from the mod's settings.
4. **What the framework dependency means.** One line: the role needs the Ectype Framework
   package, which the mod manager installs automatically and which a hand installer has
   to add.

The Void Crew community's categories include `All Required` and `Disables Progression`,
which describe a role mod's requirements. The list changes over time: read the live list
(`https://thunderstore.io/api/experimental/community/void-crew/category/`) before you set
categories.

A listing skeleton for the multiplayer section:

```markdown
### Multiplayer

- Everyone in the room needs this mod and the Ectype Framework, at the same version.
- Hosting or joining a room with this mod marks the session as modded: no progression and
  no achievements for anyone in it.
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
