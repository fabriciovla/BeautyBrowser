# Publishes a release: builds the installer, tags vX.Y.Z and uploads it to GitHub Releases, where installed copies of
# Beauty Browser find it and update themselves (see UpdateService).
#
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1              (everything)
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -SkipBuild   (reuses artifacts\BeautyBrowserSetup.exe)
#
# Before running it: raise <Version> in version.props, merge to main and push. It runs here, not on GitHub Actions,
# because releases ship CEF with proprietary codecs (third_party\cef-codecs, built by tools\build-cef-codecs.ps1),
# which only exists on this machine.
param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $root

$version = ([xml](Get-Content (Join-Path $root "version.props"))).Project.PropertyGroup.Version
$tag = "v$version"
$artifacts = Join-Path $root "artifacts"
$setup = Join-Path $artifacts "BeautyBrowserSetup.exe"
$checksum = "$setup.sha256"
$codecs = Join-Path $root "third_party\cef-codecs\libcef.dll"

Write-Host "Beauty Browser $version" -ForegroundColor Cyan

# 1. Checks: from an up-to-date main, a version that was not published yet, CEF with codecs.
if ((git branch --show-current) -ne "main") { throw "Releases are published from main." }
if (git status --porcelain) { throw "There are uncommitted changes." }
git fetch origin main --tags --quiet
if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw "main is not the same as origin/main: pull or push first." }
if (git tag --list $tag) { throw "$tag already exists: raise <Version> in version.props." }
if (-not (Test-Path $codecs)) { throw "third_party\cef-codecs is missing: releases ship CEF with codecs (tools\build-cef-codecs.ps1)." }
# What the release changes, from CHANGELOG.md: every release has to say what it does.
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw -Encoding utf8
$section = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($version))\][^\n]*\n(.*?)(?=^## \[|\z)")
if (-not $section.Success -or -not $section.Groups[1].Value.Trim()) { throw "CHANGELOG.md has no section for ${version}: write what this release changes." }
$notes = Join-Path $artifacts "release-notes.md"
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not signed in: run 'gh auth login'." }

# 2. Build.
if (-not $SkipBuild) {
    & (Join-Path $root "installer\build-installer.ps1")
}
if (-not (Test-Path $setup)) { throw "$setup is missing." }
if ((Get-FileHash (Join-Path $artifacts "app\libcef.dll")).Hash -ne (Get-FileHash $codecs).Hash) {
    throw "The published app does not carry CEF with codecs."
}
$setupVersion = (Get-Item $setup).VersionInfo.ProductVersion
if ($setupVersion -ne $version) { throw "The installer says $setupVersion, version.props says ${version}: build it again." }

# 3. Checksum: installed copies only run an installer whose SHA-256 matches.
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  BeautyBrowserSetup.exe" -NoNewline -Encoding ascii
Write-Host "SHA-256 $hash"

# 4. Tag and release.
git tag -a $tag -m "Beauty Browser $version"
git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "Could not push $tag." }
New-Item -ItemType Directory -Force $artifacts | Out-Null
Set-Content -Path $notes -Value $section.Groups[1].Value.Trim() -Encoding utf8
gh release create $tag $setup $checksum --title "Beauty Browser $version" --notes-file $notes
if ($LASTEXITCODE -ne 0) { throw "Could not create the release (the tag $tag is already pushed)." }

$mb = [math]::Round((Get-Item $setup).Length / 1048576)
Write-Host "Published $tag ($mb MB). Installed copies pick it up within 6 hours, or at their next start." -ForegroundColor Green
