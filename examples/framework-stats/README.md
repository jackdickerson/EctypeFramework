# framework-stats

F6 toggles a +50% walking top speed on the local character, by contributing a modifier against `Ectype.FrameworkStats.MotorAcceleration`. The framework registers it on every character whether or not a role mod is installed.

Shows:

- `FrameworkStats.EnsureRegistered()`, `FrameworkStats.Id(name)` and the `const string` stat names.
- Applying a `StatMod` to the character's stat collection with a source object, and removing it by that source.
- `MultiplayerType.Session`: the modifier changes this character's movement. Loading the mod flags the session.

Rules:

- Contribute through a modifier. Do not write the field behind the stat: the stat exists so every contributor composes through the game's own modifier stack, and a raw write is last-writer-wins against all of them.
- `Id` returns 0 before registration. The stat is wired onto the character at spawn, so check that the collection holds the id before applying. A modifier against a stat the collection does not hold applies to nothing and says nothing.
- A factor f is `ExponentialMultiplier` with an amount of f - 1.
- Stat changes are not replicated. A modifier applied on one machine affects only what that machine simulates. Apply it on every machine that needs the result.
- Keep the source object and remove the modifier with `RemoveModifier(source)`.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleFrameworkStat.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
