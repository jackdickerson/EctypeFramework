# timed-stat-contributions

After each normal arrival from the void, adds a forward-thrust bonus to the player ship that falls linearly to nothing over eight seconds, with `Ectype.TimedStatContributions`. For contrast, every player ship also gets 25% more hull for as long as it lives, with `Ectype.StatContributions`.

Shows:

- `TimedStatContributions.Start(key, collection, statAlias, type, amountAt, durationSeconds, startServerMs)` with `LinearDecay`.
- Anchoring the window to `ShipArrival.ServerMs` so every machine computes the same curve with no message.
- `StatContributions.Register` with `Target = StatTarget.PlayerShip`, `Stat = "MaxHitPoints"` and `Kind = SessionStatModifierKind.Percent`, registered in `Awake` and applied by the framework as each ship spawns, so every ship starts at its larger full hull.
- Thunderstore category `All Required`: the ship's simulation reads each machine's own copy of the stat, so every player runs it. A member without it sees a different ship.

Rules:

- Nothing is networked. Every machine that should have the effect starts it from the same replicated signal and the same server timestamp. A machine that never saw the signal, such as a late joiner, runs none.
- A factor f is `ExponentialMultiplier` with an amount of f - 1.
- The stat name must resolve on the target collection or the contribution has no effect. A name that does not resolve returns null; a name the collection does not hold logs a warning.
- Starting a live key again on the same collection replaces it. Keys are per collection and should be namespaced by the mod.
- `amountAt` is called ten times a second. Keep it allocation-free.
- A lasting contribution's `Amount` is asked on every client about every ship, so it answers from replicated state only. Its id is `<plugin GUID>/<name>` and is registered once.
- It ends when the window closes, on `End`, when `keepAlive` returns false, on a session change, or when the collection's holder is destroyed. `Start` returns null, applying nothing, when the window has already ended on this machine's clock.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game or BepInEx binary comes from NuGet.

The output is `bin\Release\ExampleTimedStat.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
