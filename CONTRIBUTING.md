# Contributing

Pull requests are welcome. For anything listed under "Needs an issue first", open an issue
before writing code.

## Building

You need:

- A .NET SDK, any recent version. Visual Studio is not required.
- Void Crew installed with BepInEx 5 and VoidManager. The build compiles against their DLLs in
  the game folder, never against NuGet copies.
- Network access on the first restore, which downloads the .NET Framework 4.7.2 reference
  assemblies.

Tell the build where the game is. The first of these that is set wins:

1. `-p:VoidCrewDir="<path>"` on the command line.
2. `VoidCrewDir` in `Local.props`, copied from `Local.props.example` (gitignored).
3. The `VOID_CREW_DIR` environment variable.
4. The Steam folder from the registry (`HKCU\Software\Valve\Steam`, `SteamPath`), plus
   `steamapps\common\Void Crew`. This finds only Steam's default library; a game in another
   library needs 1, 2 or 3.
5. The default Steam path, `C:\Program Files (x86)\Steam\steamapps\common\Void Crew`.

The folder is the one that holds `Void Crew.exe`. Then:

```
dotnet build -c Release
```

The DLL lands in `bin\Release\`. Nothing is built on GitHub, so every contributor builds
locally. [docs/setup.md](docs/setup.md) covers the project file and the examples' shared
properties.

### Dev tools build

```
dotnet build -c Release -p:DevTools=true
```

adds the `ECTYPE_DEV` compile constant and compiles the dev tools in `src/dev/`: a `[Dev]`
config section (vanilla tree dump, gene-tree layout workbench, a characterShip test perk), a
developer section on the F5 page (shown with VoidManager's debug mode on), and lifecycle
logging for every framework role ability. A dev build carries
`AssemblyMetadata("EctypeDevTools", "true")` and logs
`Ectype Framework <version> (dev tools build)` at startup. A release build compiles none of it.

Never ship a dev build, and never hand one to a player. Its bytes differ from the release build
at the same version, so the host's hash check bounces anyone in the room on the other build.

## What a pull request needs

- **A clean build.** `dotnet build -c Release` and `dotnet build -c Release -p:DevTools=true`
  both finish with 0 warnings and 0 errors.
- **A version bump.** `PLUGIN_VERSION` in `src/PluginInfo.cs` is the only place the version is
  written. Raise `z` on every change. `y` rises only in the maintainer's release commit: when
  a Thunderstore release is made, one commit raises `y`, resets `z` to 0 and is tagged, and
  the package is built from it. [docs/versioning.md](docs/versioning.md) has the full rules.
- **A changelog line** in `CHANGELOG.md` under the new version for any change a dependent mod
  can observe: new or removed API, changed behaviour, new manifest fields or stats. A change
  nobody downstream can see needs no entry.
- **Doc comments on new public members.** The build emits XML documentation that dependent
  authors see in their editor; a public member without a `///` summary ships undocumented
  (the compiler's warning for this is suppressed in the project file). If the member is
  part of the supported API, add it to [docs/api.md](docs/api.md) too.
- **Logged exceptions in patches.** Wrap every Harmony patch body in a try/catch that logs the
  exception. An exception that escapes a patch propagates into game code and breaks unrelated
  systems, often silently.
- **LF line endings.** The repository enforces them through `.gitattributes`; nothing to do
  unless the workflow reports CRLF.
- **Docs that match the code.** Update the page under `docs/` that describes the behaviour you
  changed.

A pull request is checked by a workflow that validates line endings, the example manifests
against `docs/schemas/ectype-role.schema.json`, relative Markdown links, `release/release.json`
and the heading for the current version in `CHANGELOG.md` and `release/CHANGELOG.md`. It does not compile. A change that compiles
only on your machine because of a local game folder is caught in review, so say in the pull
request which game version you tested against.

Testing is in game: a framework change is exercised by loading a role mod, switching loadouts
at the terminal and reading `BepInEx\LogOutput.log` for `[Error` lines.

## AI assistance

Much of this code and documentation was written with Claude and reviewed by the maintainer.
AI-assisted pull requests are welcome on the same terms as any other: you have built the
change, read it, and can answer questions about it.

## Declined without discussion

A pull request that changes the multiplayer type, the modded-session flag, or what the join
checks see ([docs/multiplayer.md](docs/multiplayer.md)) is declined. Open an issue instead.

## Needs an issue first

Open an issue and wait for a reply before sending a pull request that:

- changes the manifest contract version or the meaning of an existing manifest field
  ([docs/versioning.md](docs/versioning.md));
- removes or renames a public type or member.

Public API is removed only in a release that raises `x`, so such a change is scheduled by the
maintainer.

## Reporting a bug

Use the bug report form under Issues. It asks for the framework, game, BepInEx and VoidManager
versions, whether the problem happens solo or in a room, your mod list, steps to reproduce and
the relevant lines of `BepInEx\LogOutput.log`. The log is overwritten on every launch, so copy
it before relaunching. [docs/troubleshooting.md](docs/troubleshooting.md) lists the log lines
the framework writes and what each means; check it first.

## License

Contributions are released under the [MIT License](LICENSE).