# Builds installer\Output\WebLimiter-Setup-<version>.exe:
# self-contained publish of the app, then Inno Setup compilation.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$iscc = Join-Path $root 'tools\innosetup\ISCC.exe'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$publish = Join-Path $root 'publish\app'
if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\WebLimiter.App') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué ($LASTEXITCODE)" }

& $iscc /Q (Join-Path $PSScriptRoot 'WebLimiter.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC a échoué ($LASTEXITCODE)" }
Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter *.exe | Select-Object Name, @{ n = 'Mo'; e = { [math]::Round($_.Length / 1MB, 1) } }
