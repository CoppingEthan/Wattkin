# Installs (or updates) Wattkin from your own build in .\dist: build it first.
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
# (-ExecutionPolicy Bypass where Windows blocks scripts; it applies to that run only.)
# This is the same installer the downloaded Wattkin.exe runs: it copies itself to Program Files,
# adds the Start-menu shortcut and the Settings › Apps entry, and sets up start-with-Windows.
param([switch]$NoAutostart)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist\Wattkin.exe'
if (-not (Test-Path $exe)) { throw "No build found in .\dist. Run .\build.ps1 first." }

$arguments = '--install'
if ($NoAutostart) { $arguments += ' --no-autostart' }
$p = Start-Process $exe -ArgumentList $arguments -Verb RunAs -PassThru   # asks for administrator rights once
$p.WaitForExit()
if ($p.ExitCode -ne 0) { throw "Installing failed (code $($p.ExitCode)). See %LOCALAPPDATA%\Wattkin\logs\wattkin.log." }
Write-Host 'Installed to C:\Program Files\Wattkin'
