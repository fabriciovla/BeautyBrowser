<#
.SYNOPSIS
  Builds CEF (Chromium) with H.264/AAC codecs for BeautyBrowser.

.DESCRIPTION
  The CEF in the NuGet package ships without proprietary codecs, so live YouTube, X, Instagram and others do not play.
  This script builds exactly the same version (CEF 156.0.15+gcf671c0, Chromium 156.0.8078.80, the one CefSharp
  expects) with proprietary_codecs=true and copies the result to third_party\cef-codecs. When BeautyBrowser builds,
  those files replace the package's (see the UseCefWithCodecs target in BeautyBrowser.csproj).

  Resumable: if it stops, run it again with the same parameters and it continues where it left off.

  Requirements (checked before starting):
    - ~100 GB free on the -Dir drive
    - Visual Studio with "Desktop development with C++" (plus ATL/MFC)
    - Windows SDK 10.0.26100 with "Debugging Tools for Windows"
    - Git and Python 3
  Time: 6 to 14 hours with 16 GB of RAM. A large page file (32 GB or more) helps.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-cef-codecs.ps1
  powershell -ExecutionPolicy Bypass -File tools\build-cef-codecs.ps1 -Dir D:\cef
#>
param(
    [string]$Dir = 'C:\cef',      # where Chromium is downloaded and built (a short path without spaces)
    [int]$MinFreeGB = 100,
    [int]$Jobs = 0,               # parallel compile jobs; 0 picks one per ~2 GB of RAM (each clang process needs that)
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'

# Must match chromiumembeddedframework.runtime 156.0.15 (see the package README).
$CefBranch = '8078'      # Chromium 156.0.8078.x
$CefCommit = 'cf671c0'   # CEF 156.0.15+gcf671c0

$repo = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repo 'third_party\cef-codecs'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "`nERROR: $text" -ForegroundColor Red; exit 1 }

if (-not $SkipChecks) {
    Step 'Checking requirements'

    if ($Dir -match '\s') { Fail "The folder '$Dir' has spaces; Chromium does not build there. Use something like C:\cef." }

    $drive = (Split-Path $Dir -Qualifier).TrimEnd(':')
    $freeGB = [math]::Floor((Get-PSDrive $drive).Free / 1GB)
    $already = Test-Path (Join-Path $Dir 'chromium\src')
    if (-not $already -and $freeGB -lt $MinFreeGB) {
        Fail "${drive}: has $freeGB GB free and ~$MinFreeGB GB are needed. Free up space (or use -Dir on another drive)."
    }
    Write-Host "  Drive ${drive}: $freeGB GB free"

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git was not found. Install it: https://git-scm.com' }
    Write-Host "  $(git --version)"

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = if (Test-Path $vswhere) {
        & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
    if (-not $vs) { Fail 'Visual Studio with "Desktop development with C++" is missing.' }
    Write-Host "  Visual Studio: $vs"

    $debuggers = "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe"
    if (-not (Test-Path $debuggers)) {
        Fail ('"Debugging Tools for Windows" are missing. Visual Studio Installer > Modify > Individual components > ' +
              'Windows SDK; or Settings > Apps > Windows Software Development Kit > Modify > check "Debugging Tools for Windows".')
    }
    Write-Host '  Debugging Tools for Windows: OK'

    $ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    if ($ram -lt 32) {
        Write-Host "  RAM: $ram GB. It works, but slowly; a page file of 32 GB or more keeps linking from failing." -ForegroundColor Yellow
    }
}

# Python 3, through the "py" launcher or "python". The full path is stored because "py" would otherwise resolve to the
# Py function below (PowerShell is case-insensitive) and recurse until the stack overflows.
$pyExe = Get-Command py -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$pythonExe = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$python = if ($pyExe) { @($pyExe.Source, '-3') }
          elseif ($pythonExe) { @($pythonExe.Source) }
          else { Fail 'Python 3 was not found. Install it: https://www.python.org (check "Add to PATH").' }
function Py { & $python[0] @($python | Select-Object -Skip 1) @args; if ($LASTEXITCODE -ne 0) { Fail "python $($args -join ' ') failed with exit code $LASTEXITCODE" } }

Step "Downloading automate-git.py (CEF's official build script)"
New-Item -ItemType Directory -Force $Dir | Out-Null
$automate = Join-Path $Dir 'automate-git.py'
Invoke-WebRequest 'https://bitbucket.org/chromiumembedded/cef/raw/master/tools/automate/automate-git.py' -OutFile $automate -UseBasicParsing

# automate-git runs autoninja without -j, one process per core. With 16 GB that exhausts the RAM and clang crashes
# with 0xC0000005 on a different file each time, so the job count is limited by memory.
if ($Jobs -le 0) {
    $ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $Jobs = [math]::Max(2, [math]::Min([Environment]::ProcessorCount, [math]::Floor($ramGB / 2)))
}
$automateText = Get-Content $automate -Raw
$patched = $automateText.Replace("command = 'autoninja '", "command = 'autoninja -j $Jobs '")
if ($patched -eq $automateText) {
    Write-Host '  Warning: could not limit parallel jobs (automate-git.py changed).' -ForegroundColor Yellow
} else {
    Set-Content $automate $patched -NoNewline -Encoding UTF8
    Write-Host "  Parallel jobs: $Jobs (change it with -Jobs N)"
}

# Some options depend on the script's version, so they are only passed when they exist.
$help = (& $python[0] @($python | Select-Object -Skip 1) $automate --help) -join "`n"
$flags = @(
    "--download-dir=$Dir",
    "--depot-tools-dir=$Dir\depot_tools",
    "--branch=$CefBranch",
    "--checkout=$CefCommit",
    '--x64-build',
    '--no-debug-build',
    # automate-git only builds when the code changed or out\ does not exist, so a previous run that stopped after
    # creating it would skip the build. Builds are incremental: finished work is not repeated.
    '--force-build'
)
foreach ($optional in '--no-chromium-history', '--minimal-distrib-only', '--no-distrib-archive', '--no-distrib-docs') {
    if ($help -match [regex]::Escape($optional)) { $flags += $optional }
}
if ($flags -notcontains '--minimal-distrib-only' -and $help -match '--minimal-distrib') { $flags += '--minimal-distrib' }

# proprietary_codecs + ffmpeg_branding=Chrome: H.264/AAC/MP4, the same as Google Chrome.
# symbol_level=0 and no ThinLTO: much less disk and RAM while linking (16 GB is not enough otherwise).
$env:GN_DEFINES = @(
    'is_official_build=true',
    'proprietary_codecs=true',
    'ffmpeg_branding=Chrome',
    'symbol_level=0',
    'blink_symbol_level=0',
    'v8_symbol_level=0',
    'use_thin_lto=false',
    'chrome_pgo_phase=0'
) -join ' '
$env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'   # use the installed Visual Studio; Google's toolchain is internal

# Chromium only looks for VS 2022 under "C:\Program Files", while Build Tools install under "Program Files (x86)",
# so the real path from vswhere is passed through the variable Chromium understands.
$vswhereExe = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = if (Test-Path $vswhereExe) {
    & $vswhereExe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$vsVersion = if (Test-Path $vswhereExe) {
    & $vswhereExe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property catalog_productLineVersion
}
if ($vsPath -and $vsVersion) {
    Set-Item "env:vs${vsVersion}_install" $vsPath
    $env:GYP_MSVS_VERSION = $vsVersion
    Write-Host "  Visual Studio $vsVersion for Chromium: $vsPath"
}

# Every Chromium version requires a specific Windows SDK (build\vs_toolchain.py). If the source is already here it is
# checked now: better to find out before hours of building.
$toolchainPy = Join-Path $Dir 'chromium\src\build\vs_toolchain.py'
if (Test-Path $toolchainPy) {
    $sdkLine = Select-String -Path $toolchainPy -Pattern "^SDK_VERSION = '([0-9.]+)'" | Select-Object -First 1
    if ($sdkLine) {
        $sdk = $sdkLine.Matches[0].Groups[1].Value
        $sdkInclude = "${env:ProgramFiles(x86)}\Windows Kits\10\Include\$sdk\um"
        if (-not (Test-Path $sdkInclude)) {
            $build = $sdk.Split('.')[2]
            Fail ("This Chromium needs Windows SDK $sdk, which is not installed. Install it with:`n" +
                  "  winget install --id Microsoft.WindowsSDK.10.0.$build -e`n" +
                  "and run this script again (it continues where it left off).")
        }
        Write-Host "  Windows SDK $sdk`: OK"
    }
}
$env:GN_ARGUMENTS = '--ide=vs2022 --sln=cef --filters=//cef/*'

Step "Building CEF $CefBranch @ $CefCommit with codecs (this takes hours; it can be stopped and resumed)"
Write-Host "  GN_DEFINES = $env:GN_DEFINES"
Write-Host "  automate-git.py $($flags -join ' ')"
$started = Get-Date
Py $automate @flags
Write-Host ("  Finished in {0:hh\:mm} h" -f ((Get-Date) - $started))

Step "Copying binaries to $outDir"
$distrib = Get-ChildItem (Join-Path $Dir 'chromium\src\cef\binary_distrib') -Directory -Filter 'cef_binary_*windows64*' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $distrib) { Fail 'The built distribution was not found in chromium\src\cef\binary_distrib.' }
Write-Host "  From $($distrib.FullName)"

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force $outDir | Out-Null
Get-ChildItem (Join-Path $distrib.FullName 'Release') -File |
    Where-Object { $_.Extension -in '.dll', '.bin', '.json' } |
    Copy-Item -Destination $outDir
Copy-Item (Join-Path $distrib.FullName 'Resources\*') -Destination $outDir -Recurse -Force

if (-not (Test-Path (Join-Path $outDir 'libcef.dll'))) { Fail 'libcef.dll did not end up in third_party\cef-codecs.' }
$version = (Get-Item (Join-Path $outDir 'libcef.dll')).VersionInfo.ProductVersion
Write-Host "  libcef.dll $version"

Step 'Done'
Write-Host '  Build BeautyBrowser as usual: it will use this CEF (the build says "Using CEF with codecs").'
Write-Host "  To get the space back you can delete $Dir (about 80-100 GB). Another CEF version needs a new build."
