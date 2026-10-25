# interaction-gate

Only the room's host may take the helm, with `Ectype.InteractionGate`.

Shows:

- `InteractionGate.Register(id, gate)` at `Awake` and `Unregister(id)` in `OnDestroy`. The id is `"<plugin GUID>/<name>"`, because ids are unique across every mod.
- A gate that refuses one `InteractionKind` (`Helm`) for one kind of actor, with `Refusal.Because(reason, text)`, and allows everything else with `Refusal.Allowed`.
- Thunderstore category `All Required`: the gate runs on the acting player's game for the prompt and the warning, and on the seat owner's game for authority. A client that skipped its own check would see the helm's prompt and have its request dropped by the host with no word, so every player runs the mod. Nothing in the game compares this mod's version at join; the Void Crew Mod Launcher installs the host's version for each joiner.

Rules:

- A gate is asked on both games for one interaction, so it reads only what both agree on. This one reads the actor's identity alone.
- A gate only refuses. Another mod's gate can refuse more, and never less.
- A gate runs every frame while the player looks at something it judges. Keep it cheap.
- A player already at the helm when the gate is registered stays there: the gate judges the takeover, not the sitting.

[`docs/reference/interaction-gate.md`](../../docs/reference/interaction-gate.md) covers the kinds, both sides, the warning throttle and the uses.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed.

The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game or BepInEx binary comes from NuGet.

## Install

Copy `bin\Release\ExampleInteractionGate.dll` to `<game>\BepInEx\plugins\` on every player's machine. `BepInEx\LogOutput.log` carries `Example Interaction Gate v0.1.0 loaded: the helm is host-only.` and the framework's `interaction gate '<guid>.host-helm' registered (1 gate).`

To check it, join a host's room as a second player and look at the helm's seat from close by: no prompt shows, and the warning `ONLY THE HOST MAY PILOT THIS SHIP.` appears. The host still takes the helm as usual. A request that reaches the host anyway logs `interaction gate '<guid>.host-helm' refused Helm <seat> by player <n> (Owner): helm is host-only (further refusals of this kind to this player are logged in verbose mode only).` there.
