# character-statuses

Press F6 to take half damage for ten seconds, and F7 to slow the nearest crewmate within 5 m for three seconds, with `Ectype.CharacterStatuses`.

Shows:

- `CharacterStatuses.Register(new CharacterStatusDef(key) { ... }.AddAllVulnerabilities("flat", -0.5f))`, then `Apply(player, key, seconds)` on this player's own character.
- A status other players can request: `AcceptsRequests = true` with `Stance = StatusStance.Friendly`, `MaxRange`, `RequiresLineOfSight`, `RefuseInTube`, `MinInterval` and `Burst`, and `Request(crewmate, key, seconds).OnCompleted(...)`, which reports the verdict.
- `Presented = true`: other players see the slow as a presentation copy, which `CharacterStatuses.IsPresented` answers for.
- `MultiplayerType.All`: the slowed player's client registers the template to judge and apply the request, and every client that should see the copy registers it too. Applying a status changes what a run contains, so the plugin sets the modded-session flag.

Rules:

- A status lives on the client that owns the character. Character damage and movement resolve there, so a modifier there is the one that is read.
- `Apply` runs where it is called. On another player's character it changes nothing for them; `Request` is the call for that.
- A request goes to the target's client alone. That client checks it against its own view of both characters: the template accepts requests, both characters are alive, neither is in a clone tube, the stance, the distance, the line of sight and the rate. Only then does it apply the status, with the same callbacks a local `Apply` runs, and it answers either way. The requesting client runs the same checks first, so a refusal from them is in before `Request` returns, and nothing is sent.
- `MaxRange` is measured on the slowed player's client. The requesting client allows 15% or 1 m more, so a request at the edge reaches the slowed player's client, which decides.
- A request never shortens a slow already running, or weakens it.
- `OnApplied` and `OnRemoved` run on the slowed player's client only. A presentation copy applies no stat and runs no callback.
- Only the client that owns a character can remove a status on it. `TryRemove` on a crewmate returns `Authority`.
- A stat entry bites only a stat the character registers. `AddAllVulnerabilities` covers every damage type plus the aggregate; `MotorAcceleration` is a framework stat on every character.
- A status ends when its character dies. The game keeps the same character object through the respawn, which starts with no status. Use `RemoveAll(player)` on a role switch.
- The framework provides no way to find a target. This sample uses `LocalPlayer.I`, the local `Player`, and the nearest living entry of `ClientGame.Current.Players` that `Topology.Hostile` does not put on another side.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleCharacterStatuses.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against a framework that has `CharacterStatuses.Request` returning a handle once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
