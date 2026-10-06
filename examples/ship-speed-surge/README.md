# ship-speed-surge

After each normal arrival from the void, raises the player ship's top speed by half and lets it decay to normal over fifteen seconds, with `Ectype.ShipSpeedSurge`.

Shows:

- `ShipSpeedSurge.Start(arrival, magnitude, durationSeconds)` from `ShipArrivals.Arrived`.
- `MultiplayerType.Session`: every machine must start the surge. Loading it flags the session.

Rules:

- Start it on `Normal` arrivals only. The surge cuts the ship's drag in every direction, so an interdiction's backward fling would carry the same way.
- Start it on every machine. Each machine's simulation reads the ship's drag and stat changes are not replicated. A machine that joined after the arrival has no surge, and if it then takes the helm the room's authoritative simulation has none, so the surge ends early for everyone.
- Magnitude 1 doubles top speed at the start and a negative magnitude above -1 slows the ship. The speed factor is `1 + magnitude * (1 - progress)`.
- One surge per ship; starting another replaces it. It refuses to start outside normal flight and ends when the ship leaves it.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleSpeedSurge.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
