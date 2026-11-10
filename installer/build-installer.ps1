# Builds the installer: artifacts\BeautyBrowserSetup.exe
#
#   powershell -File installer\build-installer.ps1            (everything)
#   powershell -File installer\build-installer.ps1 -SkipApp   (reuses the already published artifacts\app)
#
# 1. Publishes the app self-contained (.NET, Windows App SDK and Chromium inside).
# 2. Compresses it into installer\BeautyInstaller\payload.zip.
# 3. Publishes the installer as a single executable that carries that zip.
param([switch]$SkipApp)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root      = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifacts = Join-Path $root "artifacts"
$app       = Join-Path $artifacts "app"
$payload   = Join-Path $PSScriptRoot "BeautyInstaller\payload.zip"
$setupOut  = Join-Path $artifacts "installer"

if (-not (Test-Path (Join-Path $root "assets\BeautyBrowser.ico"))) { & (Join-Path $PSScriptRoot "make-icon.ps1") }

if (-not $SkipApp) {
    Write-Host "1/3 Publishing BeautyBrowser…" -ForegroundColor Cyan
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    dotnet publish (Join-Path $root "src\BeautyBrowser\BeautyBrowser.csproj") -c Release -r win-x64 --self-contained true -o $app
    if ($LASTEXITCODE -ne 0) { throw "Could not publish the app." }
}
foreach ($required in "BeautyBrowser.exe", "libcef.dll", "locales\es-419.pak", "BeautyBrowser.ico") {
    if (-not (Test-Path (Join-Path $app $required))) { throw "$required is missing from the published app." }
}

Write-Host "2/3 Compressing the app…" -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "3/3 Building the installer…" -ForegroundColor Cyan
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot "BeautyInstaller\BeautyInstaller.csproj") -c Release -o $setupOut
if ($LASTEXITCODE -ne 0) { throw "Could not build the installer." }

$setup = Join-Path $artifacts "BeautyBrowserSetup.exe"
Copy-Item (Join-Path $setupOut "BeautyBrowserSetup.exe") $setup -Force
$mb = [math]::Round((Get-Item $setup).Length / 1048576)
Write-Host "Done: $setup ($mb MB)" -ForegroundColor Green
