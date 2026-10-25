<#
.SYNOPSIS
Offline checks for EctypeFramework.dll against the installed game, without starting it.

.DESCRIPTION
Builds the framework (Release), then runs tools\OfflineChecks, which reads the built DLL and the
game's assemblies with Mono.Cecil and checks:
  1. every Harmony patch target and by-name reflection lookup resolves in the game, with the
     parameter and member types the framework expects, and every patch method's injected
     parameters fit its target;
  2. the public API matches docs\api-snapshot.txt;
  3. members bound by name in other plugins (Void Crew Fleet's FleetApi, VoidManager) exist with
     the probed signatures, when the plugin is installed or passed with -Plugin.
Ends with "ALL CHECKS PASSED" and exit code 0, or the failures and exit code 1.

The game folder is the one the framework's build uses (Directory.Build.props: -VoidCrewDir,
Local.props, VOID_CREW_DIR, the Steam registry path, the default Steam path).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\offline-checks.ps1
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\offline-checks.ps1 -Plugin "<path>\VoidCrewFleet.dll"
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\offline-checks.ps1 -UpdateSnapshot
#>
param(
    # The folder that holds Void Crew.exe. Default: what the framework's build resolves.
    [string]$VoidCrewDir,
    # A plugin DLL to check bindings against, in addition to those under BepInEx\plugins.
    [string[]]$Plugin = @(),
    # Rewrite docs\api-snapshot.txt from the built DLL instead of comparing against it.
    [switch]$UpdateSnapshot,
    # Use the DLL already in bin\Release instead of building it.
    [switch]$NoBuild,
    # List every check, not only the failures.
    [switch]$ShowAll
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'EctypeFramework.csproj'

if (-not $VoidCrewDir) {
    $VoidCrewDir = (& dotnet msbuild $project -nologo "-getProperty:VoidCrewDir" | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $VoidCrewDir) {
        Write-Output "Could not read VoidCrewDir from the framework's build; pass -VoidCrewDir."
        exit 2
    }
}

if (-not $NoBuild) {
    Write-Output "== Building the framework (Release)"
    # --no-incremental: a dev tools build writes to the same bin\Release, with a different
    # public surface.
    & dotnet build $project -c Release -nologo -v:quiet --no-incremental "-p:VoidCrewDir=$VoidCrewDir"
    if ($LASTEXITCODE -ne 0) {
        Write-Output "BUILD FAILED"
        exit 1
    }
}

$checkerArgs = @(
    '--framework', (Join-Path $root 'bin\Release\EctypeFramework.dll'),
    '--game', $VoidCrewDir,
    '--targets', (Join-Path $PSScriptRoot 'OfflineChecks\manual-targets.txt'),
    '--snapshot', (Join-Path $root 'docs\api-snapshot.txt')
)
foreach ($p in $Plugin) { $checkerArgs += @('--plugin', $p) }
if ($UpdateSnapshot) { $checkerArgs += '--update-snapshot' }
if ($ShowAll) { $checkerArgs += '--verbose' }

& dotnet run --project (Join-Path $PSScriptRoot 'OfflineChecks') -c Release -- @checkerArgs
exit $LASTEXITCODE
