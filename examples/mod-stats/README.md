# mod-stats

Declares three stats in an embedded data file and reads them, with two framework factors, by id through the game's own `StatCollection`. The mod does not reference `EctypeFramework.dll`.

Shows:

- `stats\example.ectype-stats.json`, embedded under a resource name ending in `ectype-stats.json`: a `field` on the character (`HackSeconds`, base 60), a `factor` on the ship (`HackResistance`) and a `factor` in the session collection (`SupplyDropInterval`).
- The id formula, computed in the mod: `0x20000000 | (the first four bytes of MD5("ectype:stat:" + owner + "/" + name), little-endian, & 0x0FFFFFFF)`. The ids it logs at load match the framework's `mod stats: 3 declared` line.
- Reading the character's, the player's own crew ship's and the session's collection by id, with the mod's own constant as the value when a stat is absent. The crew ship comes from the framework's `Topology.CrewShipOf`, bound by reflection so the mod loads without the framework, When the framework is installed and `CrewShipOf` answers no ship (spectating, not yet placed, ship dying), the mod reads no ship stat, since the ship on screen may be another player's. Without the framework, the game's `PlayerShip` stands in, which is right in a room with one ship.
- The session collection found by name, `Ectype.SessionStats`, and the game's `IStatCollectionHolder` interface.
- Thunderstore category `All Required`: a perk naming a declared stat needs it declared on every client.

Rules:

- The owner is the plugin's BepInEx GUID, spelled exactly as `[BepInPlugin]` declares it. The framework refuses a stats file whose owner is not a plugin its assembly declares, or for a loose file, a plugin in its folder.
- The owner and names are part of every id. Renaming either derives new ids and every reader and perk naming the old ones reads nothing.
- The hash is case-sensitive. Spell the owner and name exactly as the file declares them.
- A field's fallback is the mod's own constant; a factor's is 1. The mod then behaves the same with and without the framework.
- The session collection exists only inside a session, and only the host contributes to it. Every client reads the host's session-start value.
- Nothing here contributes. A perk names a declared stat as `"<plugin GUID>/HackSeconds"`; code on the host contributes to a session stat with `Ectype.SessionStats.Contribute` from `SessionStats.Opening`.

`docs/reference/mod-stats.md` in the framework documents the file, the formula with a worked example, the generic framework factors and the session collection.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace every `REPLACE_ME` with your own unique plugin GUID: in `src\Plugin.cs` and as the `owner` in `stats\example.ectype-stats.json`, which must be the same string. Then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. No plugin DLL is referenced, so the build needs only the game and BepInEx. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game or BepInEx binary comes from NuGet.

The output is `bin\Release\ExampleModStats.dll`. Copy it to `<game>\BepInEx\plugins\` on every machine in the room, with the framework installed, to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the framework documents, not something this sample has been observed doing.
