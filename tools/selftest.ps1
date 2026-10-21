# Hardware self-test: first read-only checks (any MSI laptop), then (after you confirm) write tests that
# change one setting at a time and put everything back (laptops whose whole fan table Wattkin writes, like
# the Stealth 15M A11UEK), then (after another prompt) the power each MSI performance mode allows (about
# PL2 and PL1), under full load.
#   powershell -ExecutionPolicy Bypass -File .\build.ps1          # builds .\dist\Wattkin.exe
#   powershell -ExecutionPolicy Bypass -File .\tools\selftest.ps1   # all of it, with prompts (-Stage read|write|power|all)
# (-ExecutionPolicy Bypass because Windows blocks scripts by default; it applies to this run only.)
# Close any running Wattkin and ThrottleStop first; the script offers to stop YAMDCC: two programs writing
# the embedded controller (or ThrottleStop changing the power limits) would spoil the results. Asks for administrator rights once (UAC).
# Reports go to %LOCALAPPDATA%\Wattkin\selftest; every controller write is in %LOCALAPPDATA%\Wattkin\logs\wattkin.log.
param(
    [ValidateSet('read', 'write', 'power', 'all')][string]$Stage = 'all',
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\Wattkin.exe'),
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
# An error in the elevated window shows before it closes, instead of the window just vanishing.
trap {
    Write-Host "Stopped: $_" -ForegroundColor Red
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
    exit 2
}
$Exe = [IO.Path]::GetFullPath($Exe)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $child = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Stage $Stage -Exe `"$Exe`" -Elevated"
    $child.WaitForExit()
    exit $child.ExitCode
}

function Finish([int]$Code) {
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
    exit $Code
}

if (-not (Test-Path $Exe)) {
    Write-Host "No build found at $Exe. Run powershell -ExecutionPolicy Bypass -File .\build.ps1 first." -ForegroundColor Red
    Finish 2
}

$yamdcc = Get-Service yamdccsvc -ErrorAction SilentlyContinue
if ($yamdcc -and $yamdcc.Status -ne 'Stopped') {
    Write-Host 'The YAMDCC service is running: it would fight Wattkin over the embedded controller.' -ForegroundColor Yellow
    if ((Read-Host 'Stop it now, and keep it from starting with Windows (startup type Manual)? [y/N]') -notmatch '^(y|yes|o|oui)$') {
        Write-Host 'Nothing was run. To stop it yourself (as administrator): Stop-Service yamdccsvc; Set-Service yamdccsvc -StartupType Manual'
        Finish 2
    }

    Stop-Service yamdccsvc
    Set-Service yamdccsvc -StartupType Manual
    Write-Host 'YAMDCC stopped. To bring it back later: Set-Service yamdccsvc -StartupType Automatic; Start-Service yamdccsvc' -ForegroundColor Green
}

if (Get-Process Wattkin -ErrorAction SilentlyContinue) {
    Write-Host 'Wattkin is running. Close it first (tray icon > Exit, or "Wattkin.exe --exit").' -ForegroundColor Red
    Finish 2
}

if (Get-Process ThrottleStop -ErrorAction SilentlyContinue) {
    Write-Host "ThrottleStop is running: it changes the processor's power limits, which would spoil the power test. Close it first (and take it out of Windows startup)." -ForegroundColor Red
    Finish 2
}

$reports = Join-Path $env:LOCALAPPDATA 'Wattkin\selftest'
New-Item -ItemType Directory -Force $reports | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Run-Stage([string]$Name) {
    $report = Join-Path $reports "selftest-$Name-$stamp.txt"
    Write-Host ''
    Write-Host "Running the $Name tests..." -ForegroundColor Cyan
    $p = Start-Process $Exe -ArgumentList "--selftest $Name `"$report`"" -Wait -PassThru
    if (Test-Path $report) {
        foreach ($line in Get-Content $report -Encoding UTF8) {
            $color = switch -Regex ($line) { '^PASS' { 'Green' } '^FAIL' { 'Red' } '^(WARN|SKIP)' { 'Yellow' } '^==' { 'Cyan' } default { 'Gray' } }
            Write-Host $line -ForegroundColor $color
        }
    }
    Write-Host ''
    Write-Host "Report: $report"
    return $p.ExitCode
}

$code = 0
if ($Stage -in 'read', 'all') {
    $code = Run-Stage 'read'
    if ($code -eq 2) { Finish 2 }
}

function Confirm-Stage([string]$Question, [string[]]$Explanation) {
    if ($Stage -ne 'all') { return $true }
    Write-Host ''
    $Explanation | ForEach-Object { Write-Host $_ }
    return (Read-Host "$Question [y/N]") -match '^(y|yes|o|oui)$'
}

if ($Stage -in 'write', 'all' -and (Confirm-Stage 'Run the write tests?' @(
        'The write tests change one setting at a time (battery limit, performance and fan modes, keyboard',
        'light, mute lights, webcam, Cooler Boost, running from the battery, fan tables) and put each one back.',
        'Keep the charger connected and the laptop idle: it takes about 4 minutes, and the fans will be loud for a moment.'))) {
    $code = [Math]::Max($code, (Run-Stage 'write'))
}

if ($Stage -in 'power', 'all' -and (Confirm-Stage 'Measure the power limits?' @(
        'The power test loads the processor fully, about 80 seconds per MSI performance mode, with a pause',
        'in between: up to 8 minutes. The laptop gets hot and the fans loud. Keep the charger connected and',
        'close heavy apps. Turbo, the maximum processor state, frequency and EPP are lifted while it runs,',
        'then put back.'))) {
    $code = [Math]::Max($code, (Run-Stage 'power'))
}

Finish $code
