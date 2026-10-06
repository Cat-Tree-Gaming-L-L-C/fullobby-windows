<#
.SYNOPSIS
    Checks whether Hell Let Loose: Vietnam joins a server when Steam launches it with
    "+connect <ip>", the same way Fullobby launches it for seeding.

.DESCRIPTION
    Meant to be run by a volunteer on a Windows PC that has HLL: Vietnam installed. It:
      1. launches the game through Steam with +connect, exactly as Fullobby does;
      2. watches the launcher and game processes and records the command line the game
         actually received (does +connect survive the anti-cheat launcher?);
      3. asks the volunteer where the game ended up (on the server / title screen / main menu / other);
      4. if the game started, repeats the launch while the volunteer presses Esc through the
         intros and title screen, the way Fullobby's splash bypass does;
      5. finds the game's own logs (it looks for Unreal "Saved" folders under %LOCALAPPDATA%
         that changed during the test, since the game's folder name isn't published) and
         zips everything into one file to send back.
         The Windows user name, PC name, user-profile paths and Steam IDs are removed from
         everything it writes.

    Nothing is installed or changed. The only things written are the report folder and zip on
    the Desktop.

.PARAMETER Server
    Server address as IP:port (e.g. 203.0.113.10:7777). Asked for when omitted.
#>
[CmdletBinding()]
param(
    [string]$Server
)

$ErrorActionPreference = 'Stop'

$AppId          = '3079210'                       # Hell Let Loose: Vietnam on Steam
$InstallFolder  = 'Hell Let Loose - Vietnam'
$GameExeNames   = @('HLLVietnam-Win64-Shipping', 'HLL-Win64-Shipping')
$LauncherName   = 'Launch_HLL'
$WatchSeconds   = 180                             # how long to watch each launch
$SettleSeconds  = 90                              # keep watching this long after the window shows

$StartedAt  = Get-Date
$Stamp      = $StartedAt.ToString('yyyyMMdd-HHmmss')
$Desktop    = [Environment]::GetFolderPath('Desktop')
$ReportDir  = Join-Path $Desktop "fullobby-hllv-check-$Stamp"
$ReportFile = Join-Path $ReportDir 'report.txt'
New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null

# Personal details that commonly appear in game logs and process command lines. They are
# replaced before anything is written to the report or copied into the zip. Very short
# user/PC names are skipped so they don't mangle unrelated text.
$PrivacyReplacements = @(@{ Pattern = '(?<!\d)7656119\d{10}(?!\d)'; With = '<steamid>' })
if ($env:USERPROFILE) {
    $PrivacyReplacements = @(@{ Pattern = [regex]::Escape($env:USERPROFILE); With = '%USERPROFILE%' }) + $PrivacyReplacements
}
foreach ($pair in @(@($env:USERNAME, '<user>'), @($env:COMPUTERNAME, '<computer>'))) {
    if ($pair[0] -and $pair[0].Length -ge 3) {
        $PrivacyReplacements += @{ Pattern = '(?<![\w-])' + [regex]::Escape($pair[0]) + '(?![\w-])'; With = $pair[1] }
    }
}

function Protect-Privacy([string]$Text) {
    if (-not $Text) { return $Text }
    foreach ($r in $PrivacyReplacements) {
        $Text = [regex]::Replace($Text, $r.Pattern, $r.With, 'IgnoreCase')
    }
    return $Text
}

function Write-Report([string]$Line) {
    Add-Content -Path $ReportFile -Value (Protect-Privacy $Line) -Encoding UTF8
}

function Say([string]$Text, [string]$Color = 'Gray') {
    Write-Host $Text -ForegroundColor $Color
}

function Pause-ForEnter([string]$Prompt = 'Press Enter to continue') {
    [void](Read-Host $Prompt)
}

# Game processes that belong to HLL: Vietnam (not the original HLL, which may share an exe name).
function Get-VietnamGameProcesses {
    $found = @()
    foreach ($name in $GameExeNames) {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch { }
            if ($null -eq $path -or $path -like "*\$InstallFolder\*") {
                $found += $p
            }
        }
    }
    return $found
}

function Get-ProcessCommandLine([int]$ProcessId) {
    try {
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        if ($cim -and $cim.CommandLine) { return $cim.CommandLine }
        return '(not readable - likely protected by anti-cheat)'
    } catch {
        return "(not readable: $($_.Exception.Message))"
    }
}

function Find-SteamExe {
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $props = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($props.SteamExe -and (Test-Path $props.SteamExe)) { return $props.SteamExe }
            if ($props.InstallPath) {
                $exe = Join-Path $props.InstallPath 'steam.exe'
                if (Test-Path $exe) { return $exe }
            }
        } catch { }
    }
    $default = "${env:ProgramFiles(x86)}\Steam\steam.exe"
    if (Test-Path $default) { return $default }
    return $null
}

# Steam library folders, to confirm the game is installed and where.
function Find-VietnamInstall([string]$SteamExe) {
    $steamDir = Split-Path $SteamExe -Parent
    $libraries = @($steamDir)
    $vdf = Join-Path $steamDir 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libraries += $m.Groups[1].Value -replace '\\\\', '\'
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        if (Test-Path (Join-Path $lib "steamapps\appmanifest_$AppId.acf")) {
            return Join-Path $lib "steamapps\common\$InstallFolder"
        }
    }
    return $null
}

function Wait-ForGameToClose {
    while (@(Get-VietnamGameProcesses).Count -gt 0 -or @(Get-Process -Name $LauncherName -ErrorAction SilentlyContinue).Count -gt 0) {
        Say '  HLL: Vietnam is running. Close it (quit to desktop), then press Enter.' 'Yellow'
        Pause-ForEnter '  Press Enter once the game is closed'
        Start-Sleep -Seconds 3
    }
}

function Ask-Outcome {
    Say ''
    Say '  Where did the game end up?' 'Cyan'
    Say '    1 = On the server (loading screen for a map, or in the match)'
    Say '    2 = Title screen ("PRESS ANY BUTTON TO CONTINUE")'
    Say '    3 = Main menu (never tried to join)'
    Say '    4 = It tried to join but showed an error or went back to the menu'
    Say '    5 = Something else (game did not start, crashed, ...)'
    while ($true) {
        $answer = (Read-Host '  Type 1, 2, 3, 4 or 5 and press Enter').Trim()
        switch ($answer) {
            '1' { return 'joined' }
            '2' { return 'title-screen' }
            '3' { return 'main-menu' }
            '4' { return 'join-error' }
            '5' { return 'other' }
        }
        Say '  Please type just the number.' 'Yellow'
    }
}

# Launch through Steam the way Fullobby does and record what happens. Returns the outcome.
function Invoke-Launch([string]$Label, [string]$Instructions) {
    Say ''
    Say "=== $Label ===" 'Cyan'
    Say $Instructions 'White'
    Say ''
    Pause-ForEnter '  Press Enter to launch the game'

    Write-Report ''
    Write-Report "=== $Label ==="
    $launchAt = Get-Date
    Write-Report "Launch: `"$SteamExe`" -applaunch $AppId +connect $Server   at $($launchAt.ToString('HH:mm:ss'))"
    Start-Process -FilePath $SteamExe -ArgumentList @('-applaunch', $AppId, '+connect', $Server)

    Say '  Launched. Watching the game start (you can Alt-Tab back here any time; the questions'
    Say '  appear once the game has had time to settle, up to about 3 minutes).'

    $launcherSeen = $null; $gameSeen = $null; $windowSeen = $null
    $gameCmd = $null; $launcherCmd = $null
    $deadline = $launchAt.AddSeconds($WatchSeconds)
    while ((Get-Date) -lt $deadline) {
        $elapsed = [int]((Get-Date) - $launchAt).TotalSeconds

        if (-not $launcherSeen) {
            $l = @(Get-Process -Name $LauncherName -ErrorAction SilentlyContinue) | Select-Object -First 1
            if ($l) {
                $launcherSeen = $elapsed
                $launcherCmd = Get-ProcessCommandLine $l.Id
                Write-Report "  +${elapsed}s  launcher $LauncherName.exe started"
                Write-Report "           command line: $launcherCmd"
            }
        }

        $g = @(Get-VietnamGameProcesses) | Select-Object -First 1
        if ($g -and -not $gameSeen) {
            $gameSeen = $elapsed
            $gameCmd = Get-ProcessCommandLine $g.Id
            $gameExe = $null
            try { $gameExe = $g.Path } catch { }
            Write-Report "  +${elapsed}s  game $($g.ProcessName).exe started"
            Write-Report "           exe: $(if ($gameExe) { $gameExe } else { '(not readable)' })"
            Write-Report "           command line: $gameCmd"
            Say "  Game process started after ${elapsed}s."
        }
        if ($g -and -not $windowSeen -and $g.MainWindowHandle -ne [IntPtr]::Zero) {
            $windowSeen = $elapsed
            Write-Report "  +${elapsed}s  game window appeared ('$($g.MainWindowTitle)')"
            Say "  Game window appeared after ${elapsed}s."
            $deadline = (Get-Date).AddSeconds($SettleSeconds)
        }
        Start-Sleep -Seconds 1
    }

    $script:GameStarted = [bool]$gameSeen
    if (-not $gameSeen) {
        Write-Report "  game process never appeared within ${WatchSeconds}s"
        Say '  The game process did not appear.' 'Yellow'
    }
    if ($gameCmd -and $gameCmd.StartsWith('(not readable')) {
        # Anti-cheat usually hides it; that says nothing either way about +connect.
        Write-Report '  game command line contains the server address: unknown (not readable)'
    } elseif ($gameCmd) {
        $hasConnect = $gameCmd -match [regex]::Escape($Server)
        Write-Report "  game command line contains the server address: $hasConnect"
    }

    [console]::Beep(880, 300)
    $outcome = Ask-Outcome
    $note = Read-Host '  Anything else you noticed? (optional, press Enter to skip)'
    Write-Report "  RESULT: $outcome"
    if ($note) { Write-Report "  volunteer note: $note" }
    return $outcome
}

# --- Start ---

$failure = $null
try {
    Clear-Host
    Say 'Fullobby - HLL: Vietnam connect check' 'Cyan'
    Say '--------------------------------------'
    Say 'This launches Hell Let Loose: Vietnam the same way Fullobby does and records whether it'
    Say 'joins the server. It takes about 5-10 minutes. Nothing is installed or changed.'
    Say ''

    if (@(Get-Process -Name 'Fullobby' -ErrorAction SilentlyContinue).Count -gt 0) {
        Say 'Fullobby is running. Please quit it first (right-click its tray icon > Quit),' 'Yellow'
        Say 'so it cannot start or close the game during the test.' 'Yellow'
        while (@(Get-Process -Name 'Fullobby' -ErrorAction SilentlyContinue).Count -gt 0) {
            Pause-ForEnter 'Press Enter once Fullobby is closed'
        }
    }

    $SteamExe = Find-SteamExe
    if (-not $SteamExe) {
        throw 'Could not find Steam on this PC. Is it installed?'
    }
    $InstallDir = Find-VietnamInstall $SteamExe

    while (-not $Server -or $Server -notmatch '^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$') {
        if ($Server) { Say "  '$Server' doesn't look like IP:port (for example 203.0.113.10:7777)." 'Yellow' }
        $Server = (Read-Host 'Server address you were given (IP:port)').Trim()
    }

    Write-Report 'Fullobby HLL: Vietnam connect check'
    Write-Report "Started:     $($StartedAt.ToString('yyyy-MM-dd HH:mm:ss zzz'))"
    Write-Report "Windows:     $([Environment]::OSVersion.VersionString)"
    Write-Report "Steam:       $SteamExe (running: $(@(Get-Process -Name steam -ErrorAction SilentlyContinue).Count -gt 0))"
    Write-Report "Install dir: $(if ($InstallDir) { $InstallDir } else { '(not found in Steam libraries)' })"
    Write-Report "Server:      $Server"

    if (-not $InstallDir) {
        Say "HLL: Vietnam doesn't appear to be installed through Steam on this PC." 'Yellow'
        Say 'Continuing anyway in case it lives somewhere unusual.' 'Yellow'
    }

    Wait-ForGameToClose

    $script:GameStarted = $false
    $null = Invoke-Launch 'Test 1 of 2: hands off' @'
  When the game opens, DO NOT press any keys or click anything.
  Let the intro videos play out on their own and wait about 2 minutes.
  Then note where you are (on the server, the title screen, or the main menu) and come
  back to this window.
'@

    # Fullobby always presses keys through the intros, so test 2 is the one that matters most.
    if ($script:GameStarted) {
        Say ''
        Say 'Thanks! Now quit the game to the desktop for the second test.' 'Cyan'
        Wait-ForGameToClose
        $null = Invoke-Launch 'Test 2 of 2: press Esc like Fullobby does' @'
  This time, as soon as the game window appears, press Esc every second or two, for
  about 30 seconds - this is what Fullobby does automatically. Use ONLY Esc at first.
  If you are still on "PRESS ANY BUTTON TO CONTINUE" after that, press Space once.
  Then wait about a minute without touching anything, note where you end up, and come
  back to this window.
'@
        Say ''
        Say '  Did pressing Esc get you past "PRESS ANY BUTTON TO CONTINUE"?' 'Cyan'
        Say '    y = yes, Esc was enough'
        Say '    n = no, I had to press Space (or another key)'
        Say '    s = I never saw that screen'
        $esc = ''
        while ($esc -notin @('y', 'n', 's')) {
            $esc = (Read-Host '  Type y, n or s and press Enter').Trim().ToLower()
        }
        Write-Report "  Esc got past the title screen: $(switch ($esc) { 'y' { 'yes' } 'n' { 'no, needed another key' } 's' { 'title screen not seen' } })"
    } else {
        Write-Report ''
        Write-Report 'Test 2 skipped: the game did not start in test 1.'
    }

    Say ''
    Say 'Last step: please quit the game to the desktop so its log is complete.' 'Cyan'
    Wait-ForGameToClose

    # --- Game logs written during the test ---

    # The game's folder under %LOCALAPPDATA% isn't published (the first run showed it isn't
    # "HLLVietnam"), so look for any Unreal "Saved" folder that changed during the test.
    # Only top-level folders are checked, and only their names are recorded.
    $logRoots = @()
    $changedSaved = @()
    foreach ($d in @(Get-ChildItem -Path $env:LOCALAPPDATA -Directory -ErrorAction SilentlyContinue)) {
        $saved = Join-Path $d.FullName 'Saved'
        if (-not (Test-Path $saved)) { continue }
        $recent = @(Get-ChildItem -Path $saved -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $StartedAt } | Select-Object -First 1)
        if ($recent.Count -gt 0) {
            $changedSaved += $d.Name
            $logRoots += Join-Path $saved 'Logs'
        }
    }
    if ($InstallDir) { $logRoots += $InstallDir }
    Write-Report ''
    Write-Report "Unreal 'Saved' folders under %LOCALAPPDATA% changed during the test: $(if ($changedSaved.Count) { $changedSaved -join ', ' } else { '(none)' })"
    # Crash reports and anti-cheat logs are left out: they aren't needed to see whether the
    # game tried to connect, and they carry more about the PC than the game's own log.
    $skipDirs = '\\(Crashes|CrashReportClient|EasyAntiCheat|AntiCheat)\\'
    $logs = @()
    for ($i = 0; $i -lt $logRoots.Count; $i++) {
        $root = $logRoots[$i]
        if (Test-Path $root) {
            foreach ($f in @(Get-ChildItem -Path $root -Recurse -Filter '*.log' -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -ge $StartedAt -and $_.FullName -notmatch $skipDirs })) {
                # Keep the path in the copy's name so same-named logs from different folders
                # don't overwrite each other.
                $rel = if ($f.FullName.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                    $f.FullName.Substring($root.Length).TrimStart('\') -replace '[\\/]', '_'
                } else { $f.Name }
                $logs += [pscustomobject]@{ File = $f; CopyName = "$i-$rel" }
            }
        }
    }

    Write-Report ''
    Write-Report '=== Game logs ==='
    if ($logs.Count -eq 0) {
        Write-Report '(no game log written during the test was found)'
    } else {
        $logDir = Join-Path $ReportDir 'game-logs'
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $pattern = 'connect|Browse|travel|NetDriver|Network ?Failure|Travel ?Failure|PendingNet|LogNet:|LogOnline|Login|EAC|AntiCheat'
        foreach ($log in $logs) {
            $path = $log.File.FullName
            Write-Report "--- $path (lines mentioning connecting/travel)"
            try {
                $text = Get-Content -Path $path -Raw -Encoding UTF8 -ErrorAction Stop
                Set-Content -Path (Join-Path $logDir $log.CopyName) -Value (Protect-Privacy $text) -Encoding UTF8
            } catch {
                Write-Report "    (could not copy this log: $($_.Exception.Message))"
                continue
            }
            $hits = @(Select-String -Path $path -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -First 200)
            if ($hits.Count -eq 0) { Write-Report '    (none)' }
            foreach ($h in $hits) { Write-Report "    $($h.Line)" }
        }
    }
} catch {
    $failure = $_
    Say ''
    Say "Something went wrong: $($_.Exception.Message)" 'Red'
    try {
        Write-Report ''
        Write-Report "ERROR: $($_.Exception.Message)"
        Write-Report "  at: $($_.InvocationInfo.PositionMessage)"
    } catch { }
}

# --- Zip whatever was collected, even if the check stopped early ---

$zip = "$ReportDir.zip"
try {
    Compress-Archive -Path (Join-Path $ReportDir '*') -DestinationPath $zip -Force
    Say ''
    if ($failure) {
        Say 'The check stopped early, but the report is still useful.' 'Yellow'
    } else {
        Say 'All done - thank you!' 'Green'
    }
    Say 'Please send this file back to the Fullobby team:' 'Green'
    Say "  $zip" 'White'
    Say '(It contains the test results and the game''s log, which includes your in-game name.'
    Say ' Your Windows user name, PC name and Steam ID are removed first.)'
    Start-Process explorer.exe -ArgumentList "/select,`"$zip`""
} catch {
    Say ''
    Say "Could not create the zip file: $($_.Exception.Message)" 'Red'
    Say 'Please send the contents of this folder instead:' 'Yellow'
    Say "  $ReportDir" 'White'
}
Pause-ForEnter 'Press Enter to close this window'
