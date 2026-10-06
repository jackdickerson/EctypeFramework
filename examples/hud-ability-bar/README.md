# hud-ability-bar

Adds one button to the row beside the role-ability widget, with `Ectype.HudAbilityBar`. F9 logs a line and starts a five second cooldown that the button's fill shows.

Shows:

- `HudAbilityBar.Register(new HudButtonSpec { ... })` with `Id`, `Glyph`, `ReadyColor`, `Order`, and the per-frame `Key`, `Fill` and `Visible` callbacks, and `Handle.Dispose()`.
- `MultiplayerType.Client`: the bar is presentation. Nothing is networked or registered, so it cannot flag a session.

Rules:

- The bar draws the button, the key chip and the fill. The mod owns the input and the cooldown. The chip names a key but reads none.
- `Fill` is 0 when ready and 1 when just used.
- At most `HudAbilityBar.MaxButtons` (4) buttons. `Register` returns null and logs when the bar is full, the id is taken or the id is empty. `Register` is safe before the HUD exists.
- Namespace the `Id` with the plugin GUID.
- The glyph is a square texture, white on transparent, readable by the CPU. An unreadable texture is drawn as given, with one warning.
- A callback that throws hides its own button for the session. The rest of the bar keeps running.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins`, so both must already be installed. The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org; no game, BepInEx or VoidManager binary comes from NuGet.

The output is `bin\Release\ExampleHudBar.dll`. Copy it to `<game>\BepInEx\plugins\` to try it.

## Status

Compiles against the installed game once `REPLACE_ME` is replaced. Not run in game: the behaviour described above is what the API documents, not something this sample has been observed doing.
