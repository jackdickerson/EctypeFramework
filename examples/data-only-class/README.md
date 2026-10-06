# data-only-class

The smallest class mod that loads. The class is a JSON manifest embedded in the assembly: a hero perk and three perks with stat effects. The plugin only declares the dependencies and the multiplayer posture. No framework type is referenced from code.

Shows:

- `[BepInDependency("jack.voidcrew.ectype")]` and `MultiplayerType.All`.
- An embedded resource whose name ends `ectype-class.json`, which is how the framework finds the assembly.
- Perk targets `seat` and `seatShip`, a perk with levels, and an integer-valued stat (`"kind": "int"`).
- One embedded icon: `assets/steady-hand.png` (512x512, white on transparent) is the PNG for the `steady-hand` perk, named by that perk's `"icon": "steady-hand"`. The framework matches an embedded resource whose name ends in `<key>.png`, so the key is the file name without the extension. Perk icons in normal and hero cells are square 512x512; the wide cell takes 2048x512. The other perks and the class role icon name none, so the framework substitutes its own placeholders.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace every `REPLACE_ME` with your own unique value: the plugin GUID in `src\Plugin.cs` and `classId` in `manifest\ectype-class.json`, which must be the same string. Then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. The framework and VoidManager must already be in `BepInEx\plugins`.

The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game, BepInEx or VoidManager binary comes from NuGet.

## Install

Copy `bin\Release\ExampleClass.dll` to `<game>\BepInEx\plugins\`. The class appears in the gene-tree terminal once the framework has registered it.

`classId` in the manifest and `PLUGIN_GUID` in `Plugin.cs` are the same string. Changing `classId` orphans saved perk selections.
