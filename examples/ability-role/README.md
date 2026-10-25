# ability-role

The data-only role plus one coded role ability. The hero perk selects the ability, and the framework installs it, binds the ability key and runs its cooldown and duration.

Shows:

- `Ectype.RoleRegistry.RegisterAbilityFactory(roleId, factory)` called from the plugin's `Awake`. The factory runs once per character spawn.
- An `Ectype.EctypeRoleAbility` subclass with `OnApplyEffect` (channel starts) and `OnRemoveEffect` (channel ends, ability deselected, or character despawns).
- A timed effect expressed as an `Ectype.CharacterStatuses` template, registered once, applied for the channel's duration and removed on exit.
- Thunderstore category `All Required`, as for `data-only-role`. The game's asset check refuses a joiner whose role content differs from the host's.

The ability cuts incoming damage by 75% for 5 seconds on a 20 second cooldown.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace every `REPLACE_ME` with your own unique value: the plugin GUID in `src\Plugin.cs` and `roleId` in `manifest\ectype-role.json`, which must be the same string. Then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed.

The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game or BepInEx binary comes from NuGet.

## Install

Copy `bin\Release\ExampleAbilityRole.dll` to `<game>\BepInEx\plugins\`.

Rebuild against a newer framework only to use API it added; a framework update alone does not require reshipping this DLL.
