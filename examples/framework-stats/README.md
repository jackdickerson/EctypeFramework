# framework-stats

F6 toggles a +50% walking top speed on the local character, by contributing a modifier against `Ectype.FrameworkStats.MotorAcceleration`. The framework registers it on every character whether or not a role mod is installed.

The mod also adds a mutator, Hostile Surge, to the game's Mutators menu. While a run has it active, the session stat `ectype/HostileSpawnScale` is 1.5 on every client.

Shows:

- `FrameworkStats.EnsureRegistered()`, `FrameworkStats.Id(name)` and the `const string` stat names.
- Applying a `StatMod` to the character's stat collection with a source object, and removing it by that source.
- `SessionStatMutators.Register(MutatorSpec)`: a mutator whose rule is one session-stat modifier, a factor of 1.5. The framework registers the asset and makes it selectable. While the host starts a run that has the mutator selected or forced by its quest, the framework contributes the modifier before the host publishes the session's stats.
- Thunderstore category `All Required`: the mutator is registered with the game's Required effect, so every player in a room hosted with it needs the mod. The Void Crew Mod Launcher installs a mod in this category for every joiner.

Hostile Surge changes a number and nothing reads it here. The framework reads none of its generic stats. A mod that spawns hostiles multiplies its group size by `HostileSpawnScale`, read on the host from `SessionStats.Collection`, and the mutator takes effect through that mod. Without one, selecting Hostile Surge changes the published value and nothing else.

The mutator is registered with the game's Required effect. A player who does not have this mod is refused at the join screen of a room hosted with it, whether or not the run uses the mutator.

Rules:

- Contribute through a modifier. Do not write the field behind the stat: the stat exists so every contributor composes through the game's own modifier stack, and a raw write is last-writer-wins against all of them.
- `Id` returns 0 before registration. The stat is wired onto the character at spawn, so check that the collection holds the id before applying. A modifier against a stat the collection does not hold applies to nothing and says nothing.
- A factor f is `ExponentialMultiplier` with an amount of f - 1.
- Stat changes are not replicated. A modifier applied on one machine affects only what that machine simulates. Apply it on every machine that needs the result.
- Keep the source object and remove the modifier with `RemoveModifier(source)`.
- Register a mutator once, from `Awake`. Its GUID derives from the owner and key, so renaming either makes a new mutator, and a saved run that used the old one loses it.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed. The mutator needs Ectype Framework 0.30.0 or newer. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game or BepInEx binary comes from NuGet.

The output is `bin\Release\ExampleFrameworkStat.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
