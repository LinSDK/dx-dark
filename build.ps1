# Builds, tests and publishes DX Dark as a single self-contained executable (no .NET install needed),
# then copies it to ..\Releases\v<version>\ with the version in its name, plus a SHA-256 checksum file.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Set-Location $PSScriptRoot

[xml]$props = Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "DX Dark $version"

if (-not $SkipTests) {
    dotnet test tests/DxDark.Tests/DxDark.Tests.csproj -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$publish = Join-Path $PSScriptRoot 'publish'
dotnet publish src/DxDark.App/DxDark.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o (Join-Path $publish 'app') -nologo
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }

dotnet publish tools/DxDark.Cli/DxDark.Cli.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o (Join-Path $publish 'cli') -nologo
if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed.' }

$releases = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'Releases') "v$version"
New-Item -ItemType Directory -Force -Path $releases | Out-Null
$app = Join-Path $releases "DXDark-v$version.exe"
$cli = Join-Path $releases "dxdark-cli-v$version.exe"
Copy-Item (Join-Path $publish 'app\DXDark.exe') $app -Force
Copy-Item (Join-Path $publish 'cli\dxdark-cli.exe') $cli -Force

$lines = foreach ($file in $app, $cli) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $file -Leaf)"
}
Set-Content -Path (Join-Path $releases "SHA256SUMS-v$version.txt") -Value $lines -Encoding ascii

Write-Host ''
Write-Host $releases
Get-ChildItem $releases | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
