<#
.SYNOPSIS
    Verification probe for the passworded-server seed bug.

    Launches Hell Let Loose via Steam with a *password-appended* connect string,
    mirroring exactly what SteamLauncher.OpenGameAsync does
    (steam.exe -applaunch 686810 -dev +connect <ip:port>...), but with the
    candidate password syntax bolted on. Use this to confirm — empirically, on a
    live passworded server — whether HLL actually joins before we build any
    password-supply plumbing into the app.

    This does NOT touch the C# app or the backend. It's a throwaway probe.

.PARAMETER Server
    Target server as ip:port (e.g. 203.0.113.10:28015). Same value the app's
    +connect receives.

.PARAMETER Password
    The server's join password.

.PARAMETER Variant
    Which candidate connect syntax to try:
      1 = +connect <ip:port>?Password=<pw>   (Unreal URL option, capital P — try first)
      2 = +connect <ip:port>?password=<pw>   (lowercase key, in case it's case-sensitive)
      3 = +connect <ip:port> +password <pw>  (separate launch token — least likely)
    Default: 1

.EXAMPLE
    .\scripts\test-pw-connect.ps1 -Server 203.0.113.10:28015 -Password hunter2
    # then watch HLL: does it land IN the server, or stall on the password prompt / main menu?

.EXAMPLE
    .\scripts\test-pw-connect.ps1 -Server 203.0.113.10:28015 -Password hunter2 -Variant 2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Server,
    [Parameter(Mandatory = $true)] [string] $Password,
    [ValidateSet(1, 2, 3)] [int] $Variant = 1
)

$ErrorActionPreference = 'Stop'

# Resolve steam.exe the same way the app does (SteamPaths.cs: HKLM\...\Wow6432Node\Valve\Steam).
$installPath = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Wow6432Node\Valve\Steam' -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
if (-not $installPath) { throw 'Steam not found in registry (HKLM\SOFTWARE\Wow6432Node\Valve\Steam\InstallPath).' }
$steamExe = Join-Path $installPath 'steam.exe'
if (-not (Test-Path $steamExe)) { throw "steam.exe not found at $steamExe" }

$appId = '686810'  # Hell Let Loose (GameDefinition.Hll.SteamAppId)

# Build the argument list to match SteamLauncher.OpenGameAsync, with the password appended.
switch ($Variant) {
    1 { $args = @('-applaunch', $appId, '-dev', '+connect', "$Server`?Password=$Password") }
    2 { $args = @('-applaunch', $appId, '-dev', '+connect', "$Server`?password=$Password") }
    3 { $args = @('-applaunch', $appId, '-dev', '+connect', $Server, '+password', $Password) }
}

# Redact the password in what we print.
$shown = $args | ForEach-Object { $_ -replace [regex]::Escape($Password), '<pw>' }
Write-Host "Steam exe : $steamExe"
Write-Host "Variant   : $Variant"
Write-Host "Launching : steam.exe $($shown -join ' ')"
Write-Host ''
Write-Host 'Watch HLL: a PASS = you land inside the server (loading into the match).'
Write-Host '           a FAIL = HLL shows the password prompt, bounces to main menu, or never connects.'
Write-Host ''

& $steamExe @args
Write-Host "Launched (steam.exe returned). Observe the game client now."
