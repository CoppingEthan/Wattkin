# Builds a release into .\dist (framework-dependent single file; needs the .NET 10 Desktop Runtime).
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
    -p:PublishSingleFile=true -p:DebugType=none -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Built into $dist"
