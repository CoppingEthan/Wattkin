# Builds a release into .\dist: Wattkin.exe (framework-dependent single file; needs the .NET 10 Desktop
# Runtime) and Wattkin-portable.zip (self-contained, runs from its folder).
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
# (-ExecutionPolicy Bypass where Windows blocks scripts; it applies to that run only.)
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (Test-Path $userSdk) { $dotnet = $userSdk; $env:DOTNET_ROOT = Split-Path $userSdk }
if (-not $dotnet) { throw 'The .NET 10 SDK is needed to build.' }

& $dotnet test (Join-Path $PSScriptRoot 'tests\Wattkin.Tests') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& $dotnet publish (Join-Path $PSScriptRoot 'src\Wattkin') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:WattkinPortable=false -p:DebugType=none -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The portable version brings its own .NET. Not compressed: a compressed single file unpacks into memory
# (98 MB private instead of 21). WPF's native DLLs stay next to the exe instead of being unpacked into
# %TEMP%, so nothing is written outside the folder but what Wattkin itself writes.
$portable = Join-Path $dist 'portable'
& $dotnet publish (Join-Path $PSScriptRoot 'src\Wattkin') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:WattkinPortable=true -p:DebugType=none -o $portable --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $dist 'Wattkin-portable.zip') -CompressionLevel Optimal
Remove-Item $portable -Recurse -Force
Write-Host "Built into $dist"
