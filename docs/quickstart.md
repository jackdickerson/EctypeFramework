# Quickstart

From an install with the game, BepInEx 5, VoidManager and the framework to a new role in
the gene-tree terminal. The role is the data-only example: a JSON manifest embedded in a
small plugin, with no framework code in it.

## What you need

- Void Crew with BepInEx 5 and VoidManager installed, and `EctypeFramework.dll` in
  `BepInEx\plugins\`. The build reads all four from the game folder.
- A .NET SDK, any recent version, from Microsoft. nuget.org must be reachable on the first
  restore, which downloads the .NET Framework 4.7.2 reference assemblies. No game, BepInEx or
  VoidManager binary comes from NuGet.
- The `data-only-role` folder and `Directory.Build.props` from the framework's `examples`
  folder.

[setup.md](setup.md) explains the project file and its failure modes.

## Build and install the role

1. Copy the example folder and the examples' shared property file to a working folder of
   your own, and rename the project file. The shared file, `Directory.Build.props`, holds the
   game-folder setting and the common references, and MSBuild finds it in a folder above the
   project. The role is named Warden here.

   ```powershell
   New-Item -ItemType Directory C:\src\warden | Out-Null
   Copy-Item <examples>\Directory.Build.props C:\src\warden
   Copy-Item -Recurse <examples>\data-only-role C:\src\warden\WardenRole
   cd C:\src\warden\WardenRole
   Remove-Item -Recurse -Force bin, obj -ErrorAction SilentlyContinue
   Rename-Item ExampleRole.csproj WardenRole.csproj
   ```

2. Replace the example's identifiers, using the table below. The example does not build as shipped: `src\Plugin.cs`
   has a `#error` directive that fails the build until you delete it, and the plugin GUID and
   the manifest's `roleId` are the placeholder `REPLACE_ME`.

   | Where | Identifier | Change it to |
   | --- | --- | --- |
   | `WardenRole.csproj` | `AssemblyName`, `RootNamespace` | `WardenRole`. The assembly name is the DLL's file name. |
   | `src\Plugin.cs` | `namespace`, class `ExampleVoidPlugin` | A namespace and class name of your own. |
   | `src\Plugin.cs` | `PLUGIN_GUID` (`REPLACE_ME`) and the `#error` line above it | A GUID of your own, for example `acme.voidcrew.warden`. Delete the `#error` line. |
   | `src\Plugin.cs` | `[BepInPlugin]` name, `Author`, `Description` | What players see in VoidManager's mod list. |
   | `manifest\ectype-role.json` | `roleId` (`REPLACE_ME`) | The same string as `PLUGIN_GUID`. |
   | `manifest\ectype-role.json` | `roleName`, `roleDesc`, `color`, hero `name` and `desc` | The role's title, tooltip and accent colour. |

   Leave `[BepInDependency("jack.voidcrew.ectype")]` and `MultiplayerType.All` alone.

   Three identifiers are seeds, and renaming one after players have used the role has
   consequences:

   - `roleId` seeds the tree's GUID and the GUID of every perk, and it is the sort key that
     numbers the role's tree among all installed roles. Saved perk selections refer to
     those GUIDs, so renaming it leaves every saved selection pointing at perks that no
     longer exist.
   - A perk's `key` seeds that perk's GUID the same way. Renaming a key orphans that perk's
     saved selections. A `guid` field on the perk pins the GUID across a rename.
   - `PLUGIN_GUID` is the plugin's identity to BepInEx and VoidManager. Renaming it makes the
     new build a different mod from the old one: the join check treats the old GUID as a mod
     the room lacks, and the plugin gets a new `BepInEx\config\<guid>.cfg`. A role with a
     coded ability also passes the plugin GUID to the framework as its `roleId`, so the two
     must stay equal.

   Choose all three before the first build you give to anyone.

3. Edit two perks in `manifest\ectype-role.json`. Change `name`, `desc` and the numbers in
   `mods`. Each level's list is the total at that level, so a perk that gives 15%, then 30%
   is written `0.15` and `0.30`. `stat` takes a name from [stat-names.md](stat-names.md).
   Keep the perk `key` values, and the icon name `steady-hand`, which names the embedded
   `assets\steady-hand.png`. The remaining fields are in [manifest.md](manifest.md).

   ```json
   {
     "key": "steady-hand",
     "name": "Sharper Eye",
     "desc": "While manning a <color=#FFD700>System Module</color>, its <color=#FFD700>Accuracy</color> is increased.",
     "row": 1, "col": 0, "target": "seat", "icon": "steady-hand",
     "mods": [
       [ { "stat": "Accuracy", "type": "percent", "value": 0.15 } ],
       [ { "stat": "Accuracy", "type": "percent", "value": 0.30 } ],
       [ { "stat": "Accuracy", "type": "percent", "value": 0.45 } ]
     ]
   }
   ```

4. Build. The shared property file finds the game under
   `C:\Program Files (x86)\Steam\steamapps\common\Void Crew` unless `-p:VoidCrewDir`, a
   `VoidCrewDir` in a `Local.props` beside it (copy `Local.props.example` from the examples
   folder) or the `VOID_CREW_DIR` environment variable says otherwise, in that order.

   ```powershell
   dotnet build -c Release -p:VoidCrewDir="D:\Steam\steamapps\common\Void Crew"
   ```

   The build ends with `Build succeeded.` and writes `bin\Release\WardenRole.dll`.

5. Close the game. Copy the DLL into the game's plugin folder.

   ```powershell
   Copy-Item bin\Release\WardenRole.dll "D:\Steam\steamapps\common\Void Crew\BepInEx\plugins\"
   ```

6. Launch the game and reach the main menu. The framework registers a role once the
   game's perk and stat tables have loaded, and retries every 2 seconds until they have.

## Confirm that it loaded

The log is overwritten on every launch. Copy it before you start the game again.

```powershell
Select-String -Path "D:\Steam\steamapps\common\Void Crew\BepInEx\LogOutput.log" -Pattern "Ectype|Warden"
```

A loaded role produces these lines, in this order. Each line starts with the BepInEx
prefix `[Info   :<source>]`.

| Line | Meaning |
| --- | --- |
| `Ectype Framework v<version> loaded (contract v2).` | The framework started and applied its patches. |
| `Warden Role v0.1.0 loaded.` | Your plugin's `Awake` ran. The framework's line comes first because your plugin depends on it. |
| `Ectype: discovered 1 role(s): Warden (tree <n>, WardenRole)` | The framework found your embedded manifest. The count includes every installed role mod. |
| `Ectype: role 'Warden' registered. Tree <guid> as EPerkTree <n>, <m> perk(s). registered (stage limit 9); tree def published as EPerkTree <n>` | Registration finished. |
| `Ectype VERIFY 'Warden': ok. <m> perk(s) resolve; tree asset built; tree def published.` | Every perk resolves by GUID. |

The last three lines can appear well after the first two, because registration waits for the
game's tables.

In game, press F5 to open VoidManager's menu and choose Ectype Framework. The status page
shows:

```
Pipeline: 1/1 roles registered
Warden  (tree <n>, WardenRole)
   registered (stage limit 9); tree def published as EPerkTree <n>
```

`Pipeline` counts every installed role mod, so the figures are `<n>/<n>` when other role
mods are present. The checkbox beside the role is the host's per-role suppress toggle.

Then open the gene-tree terminal on the ship. A tree titled WARDEN, in the colour from
`color`, should sit beside the stock trees. The hero banner at the top grants nothing, since
this role has no ability.

## If it did not load

- No `discovered` line, or `discovered 0 role(s).`: the framework found no manifest. The
  plugin may not have loaded, or the manifest may not have been embedded.
- A `rejected` or `failed to parse` line names the manifest and the problem.
- A `failed at [<stage>]` line names the registration stage that threw.

[troubleshooting.md](troubleshooting.md) lists each of these messages with its cause and fix.

## Before you give it to anyone

A role mod is declared `MultiplayerType.All`. Everyone in a room needs the framework and
every role mod any player has, at the same build, and hosting with the mod installed marks
the session as modded. [multiplayer.md](multiplayer.md) has the checks and the consequences.
[choosing.md](choosing.md) compares the three kinds of mod, and [publishing.md](publishing.md)
covers the Thunderstore package.
