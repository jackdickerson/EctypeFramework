# enemy-statuses

Press F6 to slow the nearest NPC ship by half for five seconds, with `Ectype.EnemyStatuses`.

Shows:

- `EnemyStatuses.Register(new EnemyStatusDef(key) { ... }.AddStat(...))` once at `Awake`, then `Apply(target, key, seconds, magnitude, attacker)`.
- `OnApplied` and `OnRemoved` callbacks.
- `MultiplayerType.All`: a peer without the template ignores the application, so every client needs the mod. Applying a status changes what a run contains, so the plugin sets the modded-session flag.

Rules:

- Register from a literal key, on every client. The key crosses the network.
- A stat entry bites only a stat the target registers. Every `OrbitObject` has `MaxHitPoints` and `Invulnerability`, NPC ships add `MaxFlightSpeed`. An unmatched stat does nothing and the framework logs one warning per status and target type.
- Re-applying refreshes the timer and compounds up to `MaxStacks`.
- Any client may apply. `OnApplied` and `OnRemoved` run on every client, `OnTick` on the target's owner only. Late joiners do not receive statuses already running.
- The framework provides no way to find a target. This sample finds one from game types: `ClientGame.Current.PlayerShip` is the player's ship, `GameSessionManager.ActiveSector` is the loaded sector, and its `TargetableShipsCategory` lists the sector's ships as `SpaceCraft`, the base type of NPC ships. The API takes any `OrbitObject`.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleEnemyStatuses.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
