#requires -version 7
<#
.SYNOPSIS
    Launch CHLL Seeding against a chosen backend without touching global env vars.

.DESCRIPTION
    Sets CHLL_SEEDING_API_URL for this process only (the app reads it via ApiConfig),
    then launches the built CHLLSeeding.exe. Defaults to localhost:3000; pass -ApiUrl
    with your LAN backend's address to test against another PC.

.PARAMETER ApiUrl
    Backend base URL. Falls back to $env:CHLL_SEEDING_API_URL, then http://localhost:3000.

.PARAMETER Configuration
    Build configuration to launch (Release or Debug). Default: Release.

.PARAMETER Build
    Build the solution first (also auto-builds if the exe is missing).

.EXAMPLE
    ./scripts/run-local.ps1 -ApiUrl http://192.168.1.50:3000

.EXAMPLE
    ./scripts/run-local.ps1 -Build        # rebuild, then run against localhost:3000
#>
[CmdletBinding()]
param(
    [string]$ApiUrl = $(if ($env:CHLL_SEEDING_API_URL) { $env:CHLL_SEEDING_API_URL } else { 'http://localhost:3000' }),

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Build
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$sln  = Join-Path $repo 'src/ChllSeeding.sln'
$exe  = Join-Path $repo "src/ChllSeeding.App/bin/x64/$Configuration/net9.0-windows10.0.22621.0/win-x64/CHLLSeeding.exe"

if ($Build -or -not (Test-Path $exe)) {
    Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
    dotnet build $sln -c $Configuration
}
if (-not (Test-Path $exe)) {
    throw "Build did not produce $exe"
}

$ApiUrl = $ApiUrl.TrimEnd('/')
$env:CHLL_SEEDING_API_URL = $ApiUrl

Write-Host ""
Write-Host "API   : $ApiUrl" -ForegroundColor Green
Write-Host "Exe   : $exe"
Write-Host "Logs  : $env:LOCALAPPDATA\CHLLSeeding\logs"
Write-Host "Launching CHLL Seeding (close the window to exit)..." -ForegroundColor Cyan
Write-Host ""

& $exe
