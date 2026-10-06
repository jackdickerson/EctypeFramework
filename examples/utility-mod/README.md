# utility-mod

A plugin that is not a role and makes one framework call: it subscribes to `Ectype.ShipArrivals.Arrived`, which fires on the local machine each time the player ship leaves the void, and logs the arrival kind and server timestamp.

Shows:

- Depending on the framework without declaring a role: no manifest, no embedded resource.
- `MultiplayerType.Client`: nothing here changes what other players see, so a room does not require the mod and it does not flag the session.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed.

The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game, BepInEx or VoidManager binary comes from NuGet.

## Install

Copy `bin\Release\ExampleUtility.dll` to `<game>\BepInEx\plugins\`. After a jump, `BepInEx\LogOutput.log` carries a line `Ship arrived from the void: Normal (server time ...)`.
