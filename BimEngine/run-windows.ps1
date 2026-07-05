#Requires -Version 5.1
<#
.SYNOPSIS
    Launch the full BimEngine pipeline on Windows: build + deploy the Revit add-in, start Revit,
    then run the API in FileDrop mode. API + Revit share one drop folder.

.DESCRIPTION
    Steps performed:
      1. Set the shared drop folder (env vars BIMENGINE_DROP + DropFolder + Transport=FileDrop).
      2. Build the Revit add-in and deploy it to the Revit Addins folder (unless -SkipAddinBuild).
      3. Launch Revit (unless -SkipRevit) so it inherits BIMENGINE_DROP.
      4. Run the API in the foreground (Ctrl+C to stop).

    Run this from the BimEngine solution folder.

.PARAMETER RevitVersion
    Revit major version (2025 or 2026). Default 2025.

.PARAMETER DropFolder
    Shared folder the API writes to and the add-in watches. Default C:\BimEngineDrop.

.PARAMETER ApiUrl
    URL the API binds to. Default http://localhost:5080.

.PARAMETER SkipAddinBuild
    Skip building/deploying the add-in (use the already-deployed one).

.PARAMETER SkipRevit
    Do not launch Revit (e.g. Revit is already open, or you only want the API).

.EXAMPLE
    .\run-windows.ps1
.EXAMPLE
    .\run-windows.ps1 -RevitVersion 2026 -DropFolder D:\drop
.EXAMPLE
    .\run-windows.ps1 -SkipAddinBuild -SkipRevit    # just the API
#>
[CmdletBinding()]
param(
    [ValidateSet('2025', '2026')]
    [string]$RevitVersion = '2025',

    [string]$DropFolder = 'C:\BimEngineDrop',

    [string]$ApiUrl = 'http://localhost:5080',

    [switch]$SkipAddinBuild,

    [switch]$SkipRevit
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# --- 0. Sanity checks ---------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $root 'BimEngine.sln'))) {
    throw "Run this from the BimEngine solution folder (BimEngine.sln not found in $root)."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet SDK not found on PATH. Install the .NET 8 SDK."
}

# --- 1. Shared drop folder + transport ----------------------------------------------------------
Write-Step "Shared drop folder: $DropFolder"
New-Item -ItemType Directory -Force -Path $DropFolder | Out-Null

# These env vars are inherited by every child process started below (Revit + API), so both sides
# agree on the folder regardless of user/temp differences.
$env:BIMENGINE_DROP = $DropFolder   # read by the Revit add-in
$env:DropFolder     = $DropFolder   # read by the API (Configuration key)
$env:Transport      = 'FileDrop'    # API publishes to the folder instead of the in-proc channel

# --- 2. Build + deploy the Revit add-in ---------------------------------------------------------
if (-not $SkipAddinBuild) {
    Write-Step "Building + deploying Revit add-in (Revit $RevitVersion)"
    $addinProj = Join-Path $root 'BimEngine.RevitAddin\BimEngine.RevitAddin.csproj'
    $revitDir  = "C:\Program Files\Autodesk\Revit $RevitVersion"
    if (-not (Test-Path (Join-Path $revitDir 'RevitAPI.dll'))) {
        throw "RevitAPI.dll not found in '$revitDir'. Install Revit $RevitVersion or pass -RevitVersion."
    }
    dotnet build $addinProj -c Debug -p:RevitVersion=$RevitVersion
    if ($LASTEXITCODE -ne 0) { throw "Add-in build failed." }

    $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    Write-Host "Deployed to: $addinsDir"
} else {
    Write-Step "Skipping add-in build (-SkipAddinBuild)"
}

# --- 3. Launch Revit ----------------------------------------------------------------------------
if (-not $SkipRevit) {
    $revitExe = "C:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe"
    if (-not (Test-Path $revitExe)) { throw "Revit not found at '$revitExe'." }

    Write-Step "Launching Revit $RevitVersion"
    # Inherits the env vars set above -> add-in watches $DropFolder.
    Start-Process -FilePath $revitExe | Out-Null

    Write-Host @"

  In Revit:
    1. Open / create a project from the ARCHITECTURAL template
       (guarantees a door family + a phase).
    2. A 'BimEngine' ribbon panel appears; its button should show:
         $DropFolder
    Leave Revit open, then come back here.

"@ -ForegroundColor Yellow
    Read-Host "Press ENTER once Revit is open with an architectural project"
} else {
    Write-Step "Skipping Revit launch (-SkipRevit)"
}

# --- 4. Run the API (foreground) ----------------------------------------------------------------
Write-Step "Starting API at $ApiUrl (FileDrop mode). Ctrl+C to stop."
Write-Host @"

  Send a request from another terminal (single line, copy-paste):

    curl.exe -X POST $ApiUrl/projects -H "Content-Type: application/json" -d "{\"floorCount\":2,\"bedrooms\":3,\"bathrooms\":2,\"plotAreaSqm\":120,\"buildingType\":\"house\"}"

  Or open Swagger:  $ApiUrl/swagger

  The add-in builds Levels/Walls/Rooms/Doors in Revit's active document.

"@ -ForegroundColor Yellow

$env:ASPNETCORE_URLS = $ApiUrl
dotnet run --project (Join-Path $root 'BimEngine.Api') --no-launch-profile
