# enemy-abilities

F7 logs every special ability on the nearest NPC ship and its turrets, with `Ectype.EnemyAbilities`. F8 applies a five second status flagged `SuspendsAbilities` to the same ship. The two APIs are in one sample because suspension has no call of its own: it is requested by an enemy status.

Shows:

- `EnemyAbilities.Describe(target)`, `IsSuspended(host)`, and the fields of `EnemyAbilityInfo` (`Name`, `Kind`, `Phase`).
- `EnemyStatusDef.SuspendsAbilities`.
- `MultiplayerType.All`, for the same reason as `enemy-statuses`. Applying a status sets the modded-session flag.

Rules:

- `Describe` is a read. Basic weapon fire and movement are not abilities and do not appear.
- While suspended, casts hold, channelled effects are lifted, cooldowns stop and nothing new starts. Everything resumes from where it stopped. Two suspending statuses on one enemy share one freeze.
- Suspension runs on every client that has the status, so register the status on every client.
- The framework provides no way to find a target. This sample finds one from game types: `ClientGame.Current.PlayerShip` is the player's ship, `GameSessionManager.ActiveSector` is the loaded sector, and its `TargetableShipsCategory` lists the sector's ships as `SpaceCraft`, the base type of NPC ships. The API takes any `OrbitObject`.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleEnemyAbilities.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
