# voidmanager-settings-page

The `utility-mod` plugin with an optional page in VoidManager's F5 menu. It logs each arrival from the void through `Ectype.ShipArrivals.Arrived`; with VoidManager installed, the page turns that logging on and off and shows how many arrivals it has seen. Without VoidManager the mod runs in full and the page is absent.

This is the only example that references VoidManager. Every other example depends on BepInEx and the framework alone.

Shows:

- `[BepInDependency("VoidCrewModdingTeam.VoidManager", BepInDependency.DependencyFlags.SoftDependency)]`: BepInEx loads VoidManager first when it is installed, and loads this plugin either way.
- `<UseVoidManager>true</UseVoidManager>` in the csproj, which adds the `VoidManager.dll` reference from `examples\Directory.Build.props`. Building needs VoidManager installed. Running does not.
- A soft dependency that stays soft. No compiled type derives from a VoidManager type or names one in a field, property, parameter or return type, because such a type fails to load without VoidManager and `Assembly.GetTypes()` on the whole assembly then throws, which breaks Harmony's type lookups and any other mod that scans loaded assemblies. VoidManager types appear only inside method bodies that run after `Chainloader.PluginInfos` shows VoidManager loaded, marked `NoInlining` so no caller compiles them in.
- The page class built at runtime. VoidManager collects `ModSettingsMenu` subclasses only from a mod that declares a `VoidPlugin`, and declaring one is a VoidManager-derived type, which would make the dependency hard. `VoidManagerPage` emits a `ModSettingsMenu` subclass whose `Name` and `Draw` call the plain `SettingsPage` class, and inserts it into VoidManager's page list from a postfix on `PluginHandler.DiscoverPlugins`.
- Thunderstore category `Client-Side`, as for `utility-mod`.

## When a mod still wants VoidManager

The framework needs nothing from VoidManager, and a mod built on it needs nothing either. Two reasons remain to use it:

- **An F5 page**, as here: in-game settings and status, drawn with Unity's `GUILayout`. Depend on VoidManager softly and build the page as this example does. A BepInEx config file covers the same settings for players without VoidManager.
- **VoidManager's own join check.** A mod that declares a `VoidPlugin` with `MultiplayerType.All` gets VoidManager's version and DLL-hash comparison between host and joiner, when both run VoidManager. Declaring a `VoidPlugin` makes VoidManager a hard dependency.

Declaring `All` changes who can join. A host with VoidManager kicks every joiner without VoidManager as soon as any mod it runs declares `All`, so that room is closed to players who do not run VoidManager, whatever the framework does. A host without VoidManager takes both. The Void Crew Mod Launcher reads a declared `All` ahead of the package's categories, and then installs both the mod and VoidManager for every joiner. Without a declaration, a role's content is still checked by the game's own asset check, with or without VoidManager, and the launcher matches versions from the Thunderstore categories. The framework's `docs/multiplayer.md` has both checks and the mixed-room rules.

## Before you build

This example does not build as committed. `src\Plugin.cs` has a `#error` directive beside the `PLUGIN_GUID` constant. Replace `REPLACE_ME` with your own unique plugin GUID, then delete the `#error` line.

## Build

```
dotnet build -c Release
```

References resolve from the game install. The game folder is the `VoidCrewDir` property in `examples\Directory.Build.props`, which every example inherits, so copy that file together with the example folder. If Void Crew is not in Steam's default library (`steamapps\common\Void Crew` under the Steam folder the registry names), set the folder by `-p:VoidCrewDir="<path>"`, in a `Local.props` beside that file (copy `Local.props.example`), or in the `VOID_CREW_DIR` environment variable. That is the order of precedence. `EctypeFramework.dll` and `VoidManager.dll` are referenced from `BepInEx\plugins` (VoidManager also from a `VoidManager` subfolder, or from the path in the `VoidManagerDll` property), so both must already be installed.

The first restore downloads the .NET Framework 4.7.2 reference assemblies from nuget.org. No game, BepInEx or VoidManager binary comes from NuGet.

## Install

Copy `bin\Release\ExampleSettingsPage.dll` to `<game>\BepInEx\plugins\`. With VoidManager installed, `BepInEx\LogOutput.log` carries `VoidManager loaded: the F5 page is added when VoidManager builds its menu.`, and F5 lists `Example Settings Page` among the settings pages and under the mod's own entry. Without it, the line reads `VoidManager not loaded: no F5 page. Everything else runs.`, and arrivals are still logged.
