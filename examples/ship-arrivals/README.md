# ship-arrivals

Logs each time the player ship leaves the void and each time it leaves normal flight, with `Ectype.ShipArrivals`.

Shows:

- Subscribing to `ShipArrivals.Arrived` and `ShipArrivals.LeftNormalFlight`, and unsubscribing in `OnDestroy`.
- `ShipArrival.Kind`, `ShipArrival.ServerMs` and `ShipArrivals.InNormalFlight(ship)`.
- `MultiplayerType.Client`: the sample only reads, so a room does not require it and it does not flag the session.

Rules:

- The events fire on every machine in the room.
- Treat `ShipArrivalKind.Unknown` as not normal. It means this machine never saw which exit ran.
- The run's first sector raises nothing, and a joiner's first read of the room's state is never an arrival. A resumed save's first arrival does fire.
- `ServerMs` is on the Photon server clock. A timed effect started from it has the same phase on every machine.
- A handler that throws is caught per subscriber, so one failing handler does not stop the others. Catch and log anyway.

`utility-mod` makes the same subscription with the smallest possible plugin.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleShipArrivals.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
