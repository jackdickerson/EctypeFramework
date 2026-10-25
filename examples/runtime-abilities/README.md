# runtime-abilities

Installs a small Opsive ability on every character at runtime and registers it with `Ectype.RuntimeAbilities`, so its start and stop replicate by key. F8 starts it on the local character. Every client logs `Ping started` and `Ping stopped` for that character.

Shows:

- `RuntimeAbilities.Register(ability, key)` after the ability is in the locomotion's `Abilities` array.
- Installing on remote characters as well as the local one.
- Thunderstore category `Mod_Session Required`: a client without the mod has no ability under the key and ignores the message, so a joiner need not have it.

Rules:

- The game replicates an ability's state by its position in the locomotion's array. An ability added after spawn breaks that unless every client installed the same abilities in the same order. A registered ability is sent on a keyed message instead, so order across clients stops mattering.
- An unregistered runtime ability still goes through the index-based RPC. Registering is what opts it in.
- The key is a literal, namespaced by the mod, and identical on every client. Do not derive it from runtime state.
- Every client installs the ability on every character. A peer resolves the key against its own copy of that character's array.
- A manually started ability needs its `Start()` called after installation, because Unity only starts abilities that were on the prefab.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game or BepInEx binary comes from NuGet.

The output is `bin\Release\ExampleRuntimeAbility.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
