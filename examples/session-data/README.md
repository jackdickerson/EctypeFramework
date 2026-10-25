# session-data

Host-syncs one config entry, `ArrivalSpread`, with `Ectype.SessionData.HostValue`, and logs the value in force on every client whenever it changes and at each arrival from the void.

Shows:

- `SessionData.HostValue(configEntry)` at `Awake`, which returns a `HostValue<float>`.
- Reading `.Value` at the moment of use, never caching it across a session start.
- `.Source` and `.IsFromHost`, which say whether the number is the host's or this client's own.
- `Changed`, raised when the value in force changes.
- Thunderstore category `All Required`: a host-owned setting changes behaviour on every client, so every player needs the mod at the host's version. Nothing in the game compares this mod's version at join; the Void Crew Mod Launcher installs the host's version for each joiner.

Rules:

- Only the host's setting counts. On the host `.Value` is its own config. On every other client it is the host's value, and the client's own config only until a host value arrives (`IsFromHost` false).
- In the hub the host publishes the value live, so changing it in the host's config reaches the room within a second.
- From the start of a mission every client, the host included, reads the value frozen into that mission's session data. A change to the host's config mid-mission takes effect at the next session the host starts.
- A late joiner reads the host's value with the session it joins into.
- A resumed saved run uses the value the run started with.
- The key, `<config file>/General.ArrivalSpread` by default, must be the same on every client, so every client must run the same mod.

`docs/reference/session-data.md` in the framework documents the channels, the late-joiner and resume paths, and the log lines.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` is referenced from `BepInEx\plugins`, so it must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game or BepInEx binary comes from NuGet.

The output is `bin\Release\ExampleSessionData.dll`. Copy it to `<game>\BepInEx\plugins\` on every machine in the room to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
