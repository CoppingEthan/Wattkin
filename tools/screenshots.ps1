# Renders the README screenshots into docs\images from the installed Wattkin (so they show real
# hardware data, which needs administrator rights). The running copy keeps control of the hardware:
# --render only reads. Asks for administrator rights once (UAC) if needed.
param([string]$Exe = (Join-Path $env:ProgramFiles 'Wattkin\Wattkin.exe'))
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $child = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Exe', "`"$Exe`"") -Verb RunAs -PassThru
    $child.WaitForExit()
    exit $child.ExitCode
}

$raw = Join-Path ([IO.Path]::GetTempPath()) "wattkin-render-$([Guid]::NewGuid().ToString('N'))"
$render = Start-Process $Exe -ArgumentList "--render `"$raw`"" -PassThru
if (-not $render.WaitForExit(240000)) { throw 'Rendering took too long.' }
if (Test-Path (Join-Path $raw 'render-error.txt')) { throw (Get-Content (Join-Path $raw 'render-error.txt') -Raw) }

# The images the README uses.
$images = Join-Path $PSScriptRoot '..\docs\images'
New-Item -ItemType Directory -Force $images | Out-Null
$wanted = 'flyout-dark', 'flyout-light', 'widget-dark', 'tray-dark', 'tray-light',
    'settings-simple-dark', 'settings-simple-light',
    'settings-expert-battery-light', 'settings-expert-profiles-dark', 'settings-expert-graphics-dark', 'settings-expert-system-light', 'settings-expert-fans-dark', 'settings-expert-debug-dark',
    'settings-expert-battery-tall-light'
foreach ($name in $wanted) {
    Copy-Item (Join-Path $raw "$name.png") (Join-Path $images "$name.png") -Force
}

[IO.Directory]::Delete($raw, $true)
"Screenshots written to $((Resolve-Path $images).Path)"
& (Join-Path $PSScriptRoot 'hero.ps1') -Images $images # the README's top image, from these
