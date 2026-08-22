<#
.SYNOPSIS
    Builds CosmicWin, copies it out of the build tree, and launches that copy elevated.

.DESCRIPTION
    CosmicWin.App.exe requires administrator (app.manifest declares requireAdministrator), and a
    running instance holds a file lock on every assembly in the directory it was started from. Run
    it straight out of bin\Debug and the next `dotnet build` or `dotnet test` fails outright:

        error MSB3027: Could not copy "CosmicWin.Layout.dll" ... The file is locked by:
        "CosmicWin.App.exe (24260)"

    An unelevated shell cannot stop an elevated process either ("Access is denied"), so the only
    way out is closing the app by hand from the tray. Launching a COPY breaks that: the build tree
    stays unlocked, and the app can stay open while tests run.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.PARAMETER SkipBuild
    Launch whatever is already in the build tree instead of rebuilding first.

.EXAMPLE
    ./scripts/run.ps1
    Build, copy to run\, and launch elevated. Accept the UAC prompt when it appears.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'CosmicWin.App\CosmicWin.App.csproj'
$framework = 'net10.0-windows10.0.19041.0'
$built = Join-Path $repo "CosmicWin.App\bin\$Configuration\$framework"
$runDir = Join-Path $repo 'run'

$running = Get-Process -Name 'CosmicWin.App' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "CosmicWin is already running (PID $($running.Id -join ', '))." -ForegroundColor Yellow
    Write-Host 'Exit it from the tray first: an elevated process cannot be stopped from here.'
    exit 1
}

if (-not $SkipBuild) {
    Write-Host "Building $Configuration..." -ForegroundColor Cyan
    dotnet build $project -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path $built)) {
    Write-Error "No build output at $built. Build first, or drop -SkipBuild."
}

# Replaced wholesale rather than merged, so a renamed or deleted assembly cannot linger and get
# loaded instead of its replacement.
if (Test-Path $runDir) { Remove-Item $runDir -Recurse -Force }
New-Item -ItemType Directory -Path $runDir | Out-Null
Copy-Item -Path (Join-Path $built '*') -Destination $runDir -Recurse -Force

$exe = Join-Path $runDir 'CosmicWin.App.exe'
Write-Host "Launching $exe (accept the UAC prompt)..." -ForegroundColor Cyan
Start-Process -FilePath $exe -Verb RunAs

Write-Host 'Running from the copy: the build tree is unlocked, so builds and tests work while it is open.' -ForegroundColor Green
