# data-only-role

The smallest role mod that loads. The role is a JSON manifest embedded in the assembly: a hero perk and five perks with stat effects. The plugin only declares its dependency on the framework. No framework type is referenced from code.

Shows:

- `[BepInDependency("jack.voidcrew.ectype")]` as the only dependency. The plugin declares nothing to VoidManager.
- Thunderstore category `All Required`. The role's tree and perks are registered in the game's required-asset set, so the game's asset check refuses a joiner whose role content differs from the host's, with or without VoidManager. The category is what makes the Void Crew Mod Launcher install the role, at the host's version, for every joiner.
- An embedded resource whose name ends `ectype-role.json`, which is how the framework finds the assembly.
- Perk targets `seat` and `seatShip`, a perk with levels, and an integer-valued stat (`"kind": "int"`).
- `fleet-shields`: target `characterFleet`, every living ship on the holder's side, with `"distribution": "split"`, so the authored +30% is shared among those ships. With one ship it all lands on that ship, as `characterShip` would.
- `boarding-drill`: a `character` perk with `"when": "onEnemyShip"`. The holder's own client decides whether its character is aboard a ship of another side and tells the other players, and every client applies the bonus only while it is. In a room with one ship it never applies.
- `"minFrameworkVersion": "0.29.0"`, the version that added the target, the distribution and the condition. An older framework rejects the role by name instead of registering it without those two perks.
- One embedded icon: `assets/steady-hand.png` (512x512, white on transparent) is the PNG for the `steady-hand` perk, named by that perk's `"icon": "steady-hand"`. The framework matches an embedded resource whose name ends in `<key>.png`, so the key is the file name without the extension. Perk icons in normal and hero cells are square 512x512; the wide cell takes 2048x512. The other perks and the role icon name none, so the framework substitutes its own placeholders.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace every `REPLACE_ME` with your own unique value: the plugin GUID in `src\Plugin.cs` and `roleId` in `manifest\ectype-role.json`, which must be the same string. Then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. The framework must already be in `BepInEx\plugins`.

The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game or BepInEx binary comes from NuGet.

## Install

Copy `bin\Release\ExampleRole.dll` to `<game>\BepInEx\plugins\`. The role appears in the gene-tree terminal once the framework has registered it.

`roleId` in the manifest and `PLUGIN_GUID` in `Plugin.cs` are the same string. Changing `roleId` orphans saved perk selections.
