# selection-gate

Tracks whether every member of the room runs the same build of this mod, with `Ectype.SelectionGate`, and logs each change of the result. A mod that ships content a member without it cannot load would refuse or revert that content where this sample logs.

Shows:

- `SelectionGate.Register(modGuid, onChanged)` with the plugin's own GUID, and a callback that reads `GateResult.Verdict`, `HostVersion` and `DescribeFailures()`.
- `MultiplayerType.Session`: rooms may contain members without the mod, which is the case the gate answers. A Session mod flags the session as modded by being loaded.

Rules:

- The GUID passed to `Register` is the mod's own plugin GUID. The host's version and hash are read from VoidManager's list for that GUID.
- Only the host computes a real result. A client gets `NotHost`. `Pending` and `Unverified` are not `Fail`: a member's mod list may not have arrived yet, or the version matches and no hash is available. Decide per verdict, not on `Failed` alone.
- The gate runs when a member joins, leaves, or the host changes, and VoidManager's own checks run only at join. A callback fires only when the result changes.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleSelectionGate.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
