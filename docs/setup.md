# Setting up a dependent mod

How to build a mod that depends on EctypeFramework on a machine that has the game and
nothing else from this project. The same project file shape serves a data-only class, a class
with code and a utility-only mod; [choosing.md](choosing.md) says which to write.

## Prerequisites

- Void Crew with BepInEx 5 and VoidManager installed. The build reads their DLLs from the
  game folder.
- `EctypeFramework.dll` in `BepInEx\plugins\`. A mod that calls framework code compiles
  against that file. A data-only class never references it, but it needs the framework
  installed to run.
- A .NET SDK, any recent version, from Microsoft. nuget.org must be reachable on the first
  restore, which downloads the .NET Framework 4.7.2 reference assemblies as the package
  `Microsoft.NETFramework.ReferenceAssemblies`. That package is the only NuGet dependency and
  carries no game or BepInEx code.

## The project file

The three starter examples ([data-only](../examples/data-only-class/),
[ability](../examples/ability-class/), [utility](../examples/utility-mod/)) and the per-API
samples share one set of settings and base references in `examples\Directory.Build.props`,
described under "The examples' shared property file" below, and each project file holds only
what is its own. The listing here is the same set written out as one standalone project file,
the form a mod outside the examples folder uses. It is the utility example's, which needs the
fewest references.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <AssemblyName>ExampleUtility</AssemblyName>
    <RootNamespace>ExampleUtility</RootNamespace>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Version>0.1.0</Version>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <DebugType>portable</DebugType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <PropertyGroup>
    <VoidCrewDir Condition="'$(VoidCrewDir)' == ''">$(VOID_CREW_DIR)</VoidCrewDir>
    <VoidCrewDir Condition="'$(VoidCrewDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Void Crew</VoidCrewDir>
    <ManagedDir>$(VoidCrewDir)\Void Crew_Data\Managed</ManagedDir>
    <PluginsDir>$(VoidCrewDir)\BepInEx\plugins</PluginsDir>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="EctypeFramework" HintPath="$(PluginsDir)\EctypeFramework.dll" Private="false" />
    <Reference Include="BepInEx" HintPath="$(VoidCrewDir)\BepInEx\core\BepInEx.dll" Private="false" />
    <Reference Include="VoidManager" HintPath="$(PluginsDir)\VoidManager.dll" Private="false" />
    <Reference Include="Assembly-CSharp" HintPath="$(ManagedDir)\Assembly-CSharp.dll" Private="false" />
    <Reference Include="UnityEngine" HintPath="$(ManagedDir)\UnityEngine.dll" Private="false" />
    <Reference Include="UnityEngine.CoreModule" HintPath="$(ManagedDir)\UnityEngine.CoreModule.dll" Private="false" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="src\*.cs" />
  </ItemGroup>

</Project>
```

Build with `dotnet build -c Release` from the project folder. If a parent folder holds a
`Directory.Build.props` or `Directory.Build.targets`, MSBuild applies it to the project;
check for one when a build behaves differently from the examples.

### Properties

| Property | Value | Reason |
| --- | --- | --- |
| `AssemblyName` | the DLL's file name without `.dll` | Two plugins whose DLLs share a name overwrite each other in `BepInEx\plugins\`. The framework also caches icons by assembly name and icon key. |
| `RootNamespace` | your namespace | It prefixes the name of each embedded resource. A manifest is found by the end of that name, so the prefix is free. |
| `TargetFramework` | `net472` | The game runs on Unity's Mono with the .NET Framework 4.x profile. |
| `LangVersion` | `latest` | Uses the newest C# the SDK's compiler offers. Features that need runtime types the .NET Framework profile lacks, such as ranges, still do not compile. |
| `Version` | equal to the plugin's `PLUGIN_VERSION` | Sets the DLL's file and assembly version. VoidManager compares the version in `[BepInPlugin]`, so the two are kept equal by hand. |
| `EnableDefaultCompileItems` | `false` | Only the files named in `Compile` build. A stray `.cs` in the folder cannot join the assembly. |
| `AppendTargetFrameworkToOutputPath` | `false` | Output goes to `bin\Release\`, with no `net472` subfolder. |
| `DebugType` | `portable` | Writes a `.pdb` beside the DLL. Nothing requires installing it. |
| `IsPackable` | `false` | Stops `dotnet pack` from treating the project as a NuGet package. |
| `VoidCrewDir` | the game folder | The one path every reference derives from. It takes the value of `-p:VoidCrewDir="<path>"` first, then the `VOID_CREW_DIR` environment variable, then the standard Steam path. |
| `ManagedDir`, `PluginsDir` | derived from `VoidCrewDir` | `Void Crew_Data\Managed` holds the game's assemblies. `BepInEx\plugins` holds VoidManager and the framework. |

### References

Each reference sets `Private="false"`, which stops MSBuild from copying the referenced DLL
into `bin\Release\`. The output folder then holds your DLL and its `.pdb` only, and the
copies in the game folder stay the only copies.

Folders are relative to the game install, the folder that holds `Void Crew.exe`. The last
column is the first compiler error you get when the reference is missing and your code
touches a type from that assembly.

| Assembly | Folder | Needed when | Error without it |
| --- | --- | --- | --- |
| `BepInEx` | `BepInEx\core` | every mod: `BaseUnityPlugin`, `[BepInPlugin]`, `[BepInDependency]`, the logger | `CS0246`, type `BepInPlugin` not found |
| `VoidManager` | `BepInEx\plugins` | every mod: `VoidPlugin`, `MultiplayerType`, the plugin GUID constant | `CS0246`, namespace `VoidManager` or type `VoidPlugin` not found |
| `UnityEngine`, `UnityEngine.CoreModule` | `Void Crew_Data\Managed` | every mod: `MonoBehaviour` is the base of `BaseUnityPlugin` | `CS0012`, type `MonoBehaviour` is defined in an assembly that is not referenced |
| `EctypeFramework` | `BepInEx\plugins` | code that calls the `Ectype` namespace. A data-only class carries no reference to it | `CS0246`, namespace `Ectype` not found |
| `Assembly-CSharp` | `Void Crew_Data\Managed` | any game type: `ClientGame`, `OrbitObject`, `Player`, the ability base class | `CS0246`, for example `OrbitObject` or namespace `CG` not found |
| `PhotonUnityNetworking` | `Void Crew_Data\Managed` | game types that are Photon behaviours, which includes `ClientGame`, `OrbitObject`, `Player` and the ability's `LocalPlayer`; `PhotonNetwork` | `CS0012`, type `MonoBehaviourPun` is defined in an assembly that is not referenced |
| `PhotonRealtime` | `Void Crew_Data\Managed` | naming a Photon room or player type, for example `PhotonNetwork.LocalPlayer` | `CS0012`, type `Player` is defined in an assembly that is not referenced |
| `ResourceAssets` | `Void Crew_Data\Managed` | `GUIDUnion`, the game's asset identifier, for example an item GUID in a loot entry | `CS0012`, type `GUIDUnion` is defined in an assembly that is not referenced |
| `VoidCrewCommon` | `Void Crew_Data\Managed` | shared game enums and data types, for example `LootRarities` | `CS0012`, type `LootRarities` is defined in an assembly that is not referenced |
| `Newtonsoft.Json` | `Void Crew_Data\Managed` | game types whose members use `JToken`, which the asset identifier does | `CS0012`, type `JToken` is defined in an assembly that is not referenced |
| `Unity.InputSystem` | `Void Crew_Data\Managed` | `UnityEngine.InputSystem`, for example `Keyboard.current` | `CS0234`, namespace `InputSystem` does not exist in `UnityEngine` |
| `Opsive.UltimateCharacterController` | `Void Crew_Data\Managed` | a class ability or any `Ability` subclass | `CS0012`, type `Ability` is defined in an assembly that is not referenced (or `CS0246`) |
| `Opsive.Shared` | `Void Crew_Data\Managed` | the same: `Ability` derives from a type in this assembly | `CS0012`, type `StateObject` is defined in an assembly that is not referenced |

A mod that touches the common game types (`ClientGame`, `OrbitObject`, `Player`,
`GUIDUnion`) references the full base set: `BepInEx`, `VoidManager`, `EctypeFramework`,
`Assembly-CSharp`, `UnityEngine`, `UnityEngine.CoreModule`, plus `PhotonUnityNetworking`,
`PhotonRealtime`, `VoidCrewCommon`, `ResourceAssets`, `Newtonsoft.Json` and
`Unity.InputSystem`. A class ability adds `Opsive.UltimateCharacterController` and
`Opsive.Shared`. A mod that names none of those types can leave the extra assemblies out.

`CS0012` names the missing assembly in its text: `You must add a reference to assembly
'ResourceAssets, Version=0.0.0.0, ...'`. Add that DLL from `Void Crew_Data\Managed` the same
way. `CS0246` and `CS0234` mean a namespace or type name resolves to no referenced assembly, and
the type's own namespace or name tells you which assembly to look in. A mod that uses another
framework area or a game type reaches for more assemblies by the same rule.

### The examples' shared property file

`examples\Directory.Build.props` is a standard MSBuild property file that applies to every
project under `examples\`. It resolves the game folder, sets the common project properties and
adds the references every example needs (`BepInEx`, `VoidManager`, `UnityEngine`,
`UnityEngine.CoreModule`). The game folder, the `VoidCrewDir` property, is taken from the first
of these that is set: `-p:VoidCrewDir="<path>"` on the command line, a `VoidCrewDir` set in an
optional, gitignored `examples\Local.props` (copy `examples\Local.props.example` and edit the
one property), the `VOID_CREW_DIR` environment variable, then the standard Steam path. The
file is read from the project's folder upward, so an example copied out of `examples\` builds
only if the props file is copied into a folder above it.

### Embedded resources

A class mod embeds its manifest, and its icons if it has any:

```xml
<EmbeddedResource Include="manifest\*.json" />
<EmbeddedResource Include="assets\*.png" />
```

The framework finds a class by an embedded resource whose name ends in
`ectype-class.json`. An icon key in the manifest names an embedded PNG, matched by the
resource name ending in `<key>.png`. A key with no PNG falls back to the framework's own
placeholder. A utility-only mod embeds nothing.

### The metadata VoidManager reads

VoidManager builds a mod's entry in its list from the plugin, not from the project file:

| Member | Source |
| --- | --- |
| Name, GUID, version | The `[BepInPlugin]` attribute. |
| `MPType` | An override on your `VoidPlugin` subclass. See [multiplayer.md](multiplayer.md) for the values and what each costs. |
| `Author`, `Description` | Overrides on the same subclass. |
| `ThunderstoreID` | An optional override on the same subclass, `Author/Package`. |
| Session flag | The return value of `OnSessionChange`. A class mod returns `SetMod_Session = true`. |

## The dependency attribute

A mod that uses the framework declares a hard dependency on its GUID, `jack.voidcrew.ectype`,
and on VoidManager:

```csharp
[BepInPlugin(PLUGIN_GUID, "My Mod", PLUGIN_VERSION)]
[BepInProcess("Void Crew.exe")]
[BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
[BepInDependency("jack.voidcrew.ectype")]
public class Plugin : BaseUnityPlugin
```

BepInEx loads a plugin after every plugin it depends on, and does not load it at all when
one is missing. Without the attribute the load order is undefined, and a call into the
framework from `Awake` can run before the framework has set up its logger and fail with a
`NullReferenceException`.

A minimum version is the second argument:

```csharp
[BepInDependency("jack.voidcrew.ectype", "0.19.7")]
```

BepInEx then refuses to load the plugin when the installed framework is older. A mod that
calls framework code should use this form with the version it was compiled against, because an
older framework without a member the mod calls fails at the first call that touches it, with
a `TypeLoadException` or `MissingMethodException` raised inside the mod. A class that only
needs a newer manifest feature can instead set `minFrameworkVersion` in the manifest, which
rejects that one class and logs a line naming the version needed. [versioning.md](versioning.md)
says what each gate covers.

## Where the output goes

`bin\Release\<AssemblyName>.dll`. Copy it to `<game>\BepInEx\plugins\` with the game closed.
Rebuild and copy again for every change; BepInEx loads plugins at launch.

The DLL embeds its manifest and icons, so it is the only file a class mod installs.

## Failure modes

**Wrong game path.** MSBuild warns `MSB3245: Could not resolve this reference. Could not
locate the assembly "BepInEx"` once for each reference under the wrong folder, then the
compiler reports `CS0246` for the namespaces that came from them. Set `VoidCrewDir` to the
folder that holds `Void Crew.exe`, by `-p:VoidCrewDir="<path>"` or the `VOID_CREW_DIR`
environment variable. A library on another drive is the usual cause, since the default is the
Steam folder on `C:`.

**A missing DLL in a correct game folder.** The same two messages appear for one reference
only. `VoidManager.dll` and `EctypeFramework.dll` come from `BepInEx\plugins`, so a game folder
that has not had VoidManager or the framework installed fails on them. Install both first.

**A reference assembly the compiler cannot find.** `CS0012` as above. Add the named DLL.

**The net472 reference assemblies are not found.** The build fails on restore when nuget.org
cannot be reached and the package is not already cached. Run the first restore with network
access. The package then sits in the NuGet cache, and later builds work offline.

**BepInEx or VoidManager taken from NuGet.** A `PackageReference` to a BepInEx package
compiles, and may be a different build from the one the game runs. A member that exists in
one and not the other shows up when the plugin loads, as a `MissingMethodException` inside
your code, not as a compile error. Reference `BepInEx\core\BepInEx.dll` and
`BepInEx\plugins\VoidManager.dll` from the install so that the code you compile against is
the code that runs it. The same goes for the framework: it is never a NuGet reference.

**A stale framework DLL.** The reference resolves to whichever `EctypeFramework.dll` sits in
`BepInEx\plugins`. If that file is older than the framework your players run, the mod
compiles against members it cannot count on, and fails at the first such call. If it is newer,
the mod records the newer assembly version and needs that framework or newer at run time.
Update the installed framework before each build, and declare the version you built against
in `[BepInDependency]`.

**A framework update changes your DLL.** A compiled mod records the assembly version of each
assembly it references, so rebuilding against a new framework changes the bytes at an
unchanged version number. Rebuild, raise the version and reship with each framework release.
A data-only class references no framework assembly, so its bytes change only when BepInEx or
VoidManager change. See [multiplayer.md](multiplayer.md).

**A BOM or trailing comma in the manifest.** The framework logs `failed to parse` with the
JSON reader's message. Save the manifest as UTF-8 without a byte order mark.
