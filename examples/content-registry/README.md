# content-registry

Publishes one new carryable with `Ectype.RuntimeContent`: a copy of the stock weapon mod "Heatsink MkI" under its own GUID, name and crafting data. It then adds the item to the loot rolls that drop weapon mods.

Shows:

- `RuntimeContent.Publish(ContentKind.Carryable, guid, new ContentAssets { ... }, new ModContentEntry { ... })`, and reading the returned handle's `State` and `Reason`.
- Crafting data on a carryable. `Publish` refuses a carryable without it.
- A dormant prefab copy taken from a stock donor. The copy is instantiated under an inactive holder so the donor's behaviours never wake. It is then kept as an inactive root that survives scene loads.
- Waiting for the game's containers before building the copy, because the donor prefab is only reachable through them.
- `LootTables.Register` for the new GUID.
- Thunderstore category `All Required`: the item is a network-spawned asset that every client in the room must have. It is registered in the game's required-asset set, so the game's asset check refuses a joiner who lacks it, with or without VoidManager.

The copy keeps the donor's model, icon, stat mods and tooltip type chips. A real item changes its stat mods, or builds its prefab some other way. The publication is the same either way.

Rules:

- Pick the item GUID once, by hand, and never change it after release. It is the item's identity on every client and in saved loadouts.
- Crafting data is mandatory for a carryable. Without it, the game's end-of-run alloy tally throws on the item, and the fabricator cannot recycle it.
- Publish before the player first opens a terminal. A catalog mod may read the item lists once per session.
- `Publish` registers every container itself. Do not also register the GUID in a container, or the publication is refused.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID and `ItemGuid` with a GUID you pick, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder in one of three ways: `-p:VoidCrewDir="<path>"`, a `Local.props` beside that file (copy `Local.props.example`), or the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game or BepInEx binary comes from NuGet.

The output is `bin\Release\ExampleContentRegistry.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` and `ItemGuid` are replaced. Not run in game. The behaviour described above is what the API documents. This sample has not been observed doing it.
