# loot-tables

Adds one more entry for an item to the stock loot rolls that already drop weapon mods, with `Ectype.LootTables`.

Shows:

- `LootTables.Register(new LootDrop { ... })` with an item GUID, a rarity from `LootTables.ToLootRarity`, an anchor (`AnyCarryableTagged("Carryable_WeaponMod")`), `RequireRarityInTable` and a `Gate`.
- A gate built on `SelectionGate`: it answers false for a room where a member fails the build check.
- `MultiplayerType.Session`: changing what drops changes what a run contains.

The item in this sample is a stock weapon mod, chosen so the sample needs no asset of its own. A mod that adds a new item registers that asset separately and passes its GUID here; that registration is not part of this API.

Rules:

- An item joins a roll only when the table holds an authored entry that the anchor accepts. It takes the smallest anchor amount and the union of the anchors' spawn locations, at its own rarity. Entries added at runtime are never anchors.
- An item alone in its rarity drops every time that rarity is asked for. Set `RequireRarityInTable = true` to keep it out of tables that offer nothing at its rarity.
- A registered item must resolve on every client. Close the `Gate` for any room where a member cannot resolve it. A gate that returns false or throws leaves the item out of that roll.
- `Register` is idempotent and returns false, with a log line, when the item or anchor is missing or another owner registered the item first.
- Rolls that spawn items run on the host.

Constructing the item GUID needs references to `ResourceAssets`, `VoidCrewCommon` and `Newtonsoft.Json`, which this project's csproj includes.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not under `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`, set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleLootTables.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
