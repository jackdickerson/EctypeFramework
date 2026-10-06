# character-statuses

Press F6 to take half damage for ten seconds, with `Ectype.CharacterStatuses`.

Shows:

- `CharacterStatuses.Register(new CharacterStatusDef(key) { ... }.AddAllVulnerabilities("flat", -0.5f))`, then `Apply(player, key, seconds)`.
- `MultiplayerType.Session`: only this player's own damage intake changes, so other members need not run the mod. Loading it flags the session.

Rules:

- A character status exists only on the client that applied it. That is correct for stats: character damage resolves on the victim's client, so a modifier there is the one that is read.
- `OnApplied`, `OnTick` and `OnRemoved` also run only where `Apply` was called. A status whose payload the crew must see needs replication, which the framework does not implement. `Replicated = true` only logs a warning.
- A stat entry bites only a stat the character registers. `AddAllVulnerabilities` covers every damage type plus the aggregate.
- Statuses are keyed on the character instance. A respawned character is a different target and a status on a destroyed one ends on the next poll. Use `RemoveAll(player)` on a class switch.
- Timing is `Time.time`, local to the client.
- The framework provides no way to find a target. This sample uses `LocalPlayer.I`, the local `Player`. Every player is in `ClientGame.Current.Players`.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleCharacterStatuses.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
