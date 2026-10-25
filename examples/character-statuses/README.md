# character-statuses

Press F6 to take half damage for ten seconds, and F7 to slow the nearest crewmate within 5 m for three seconds, with `Ectype.CharacterStatuses`.

Shows:

- `CharacterStatuses.Register(new CharacterStatusDef(key) { ... }.AddAllVulnerabilities("flat", -0.5f))`, then `Apply(player, key, seconds)` on this player's own character.
- A replicated status: `Replicated = true` with `Stance = StatusStance.Friendly`, `MaxRange`, `RequiresLineOfSight`, `MinInterval` and `Burst`, and `Request(crewmate, key, seconds)`, which returns a `CharacterStatusRefusal`.
- `Presented = true`: other players see the slow as a presentation copy, which `CharacterStatuses.IsPresented` answers for.
- `MultiplayerType.All`: the slowed player's client registers the template to judge and apply the request, and every client that should see the copy registers it too. Applying a status changes what a run contains, so the plugin sets the modded-session flag.

Rules:

- A status lives on the client that owns the character. Character damage and movement resolve there, so a modifier there is the one that is read.
- `Apply` runs where it is called. On another player's character it changes nothing for them; `Request` is the call for that.
- A request goes to the target's client alone. That client checks it against its own view of both characters: the template accepts requests, both characters are alive, the stance, the distance, the line of sight and the rate. Only then does it apply the status, with the same callbacks a local `Apply` runs. The requesting client runs the same checks first, so `Request` returns a refusal without sending anything when one fails. `None` means sent: the target's client can still refuse, and logs why.
- `MaxRange` is one metre more than the distance this client checks before sending, because the target's client sees the requester one network update late.
- `OnApplied` and `OnRemoved` run on the slowed player's client only. A presentation copy applies no stat and runs no callback.
- Only the client that owns a character can remove a status on it. `TryRemove` on a crewmate returns `Authority`.
- A stat entry bites only a stat the character registers. `AddAllVulnerabilities` covers every damage type plus the aggregate; `MotorAcceleration` is a framework stat on every character.
- Statuses are keyed on the character instance. A respawned character is a different target and a status on a destroyed one ends on the next poll. Use `RemoveAll(player)` on a role switch.
- The framework provides no way to find a target. This sample uses `LocalPlayer.I`, the local `Player`, and the nearest other entry of `ClientGame.Current.Players`.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleCharacterStatuses.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against a framework that has `CharacterStatuses.Request` once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
