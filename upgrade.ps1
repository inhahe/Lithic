<#
.SYNOPSIS
    Build the current source into an MSI and upgrade the installed Lithic Backup
    in place.

.DESCRIPTION
    This is the "productized" upgrade path. It:

      1. Runs installer\build-installer.ps1, which reads the version from
         src\Directory.Build.props (the single source of truth that also stamps
         every assembly) and produces installer\LithicBackup-<version>-x64.msi.
      2. Launches that MSI. The installer's MajorUpgrade handling stops the
         Lithic Backup Worker service, replaces the program files, and restarts
         the service.

    Because it is a real MSI upgrade, Add or Remove Programs shows the new
    version and the install stays a proper MSI install (so the in-app "check for
    updates" version comparison stays honest). Your data in
    C:\ProgramData\LithicBackup (backup sets, catalog, settings, logs) is
    preserved across the upgrade.

    Compared with deploy.ps1: deploy.ps1 is a fast developer loop that mirrors a
    fresh publish over the install folder with no version bump and no Add/Remove
    Programs update. Use THIS script when you want a clean, versioned upgrade
    that matches what an end user would get from the released MSI.

    The build step runs unelevated; the MSI install self-elevates via UAC (it is
    a per-machine install), so expect a UAC prompt when the install starts.

.PARAMETER Configuration
    Build configuration passed through to the installer build. Default Release
    (matches the shipped MSI).

.PARAMETER Silent
    Install with only a basic progress bar and no prompts (msiexec /qb).
    Otherwise the full install wizard is shown.

.PARAMETER SkipBuild
    Skip building and upgrade using the newest existing installer\*.msi. Useful
    to re-run an install you already built.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File upgrade.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File upgrade.ps1 -Silent

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File upgrade.ps1 -SkipBuild
#>
param(
    [string]$Configuration = "Release",
    [switch]$Silent,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host $msg -ForegroundColor Cyan }

$here         = Split-Path -Parent $MyInvocation.MyCommand.Definition
$installerDir = Join-Path $here "installer"
$buildScript  = Join-Path $installerDir "build-installer.ps1"

Write-Host "=== Lithic Backup upgrade ($Configuration) ===" -ForegroundColor Green

# --- 1. Build the MSI from the current source ---------------------------------
if (-not $SkipBuild) {
    if (-not (Test-Path $buildScript)) {
        throw "Cannot find installer build script at '$buildScript'."
    }
    Write-Step "Building installer from current source..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit $LASTEXITCODE)." }
}
else {
    Write-Step "Skipping build (-SkipBuild); using the newest existing MSI."
}

# --- 2. Locate the MSI to install ---------------------------------------------
# build-installer.ps1 writes LithicBackup-<version>-x64.msi into .\installer.
# Pick the most recently written one so this works regardless of version.
$msi = Get-ChildItem -Path $installerDir -Filter "LithicBackup-*-x64.msi" -File -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1
if (-not $msi) {
    throw "No installer\LithicBackup-*-x64.msi found. " +
          "Run without -SkipBuild to build one first."
}
Write-Host "  MSI : $($msi.FullName)"
Write-Host ("  Size: {0:N1} MB" -f ($msi.Length / 1MB))

# --- 3. Run the MSI (self-elevates via UAC) -----------------------------------
$log = Join-Path $env:TEMP ("lithic-upgrade-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
$msiArgs = @("/i", "`"$($msi.FullName)`"", "/L*v", "`"$log`"")
if ($Silent) { $msiArgs += "/qb" }

Write-Step "Launching installer (a UAC prompt will appear)..."
$proc = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArgs -Verb RunAs -Wait -PassThru

# --- 4. Report ----------------------------------------------------------------
Write-Host ""
switch ($proc.ExitCode) {
    0     { Write-Host "=== Upgrade complete ===" -ForegroundColor Green }
    3010  { Write-Host "=== Upgrade complete (a reboot is required to finish) ===" -ForegroundColor Green }
    1602  { Write-Host "Upgrade cancelled by the user." -ForegroundColor Yellow }
    default {
        Write-Host "Upgrade FAILED (msiexec exit code $($proc.ExitCode))." -ForegroundColor Red
        Write-Host "See the verbose install log: $log"
        exit $proc.ExitCode
    }
}
Write-Host "Install log: $log"
