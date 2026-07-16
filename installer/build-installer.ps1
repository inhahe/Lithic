<#
.SYNOPSIS
    Builds the LithicBackup MSI installer.

.DESCRIPTION
    1. Publishes the GUI and the Worker service as a single self-contained
       win-x64 bundle into .\publish (no .NET runtime prerequisite on the
       target machine).
    2. Compiles Package.wxs into an MSI with the WiX Toolset (`wix` dotnet tool).

    Prerequisites (installed automatically if missing):
      * WiX dotnet tool:  dotnet tool install --global wix
      * WiX UI extension: wix extension add -g WixToolset.UI.wixext

.PARAMETER Version
    Product version stamped into the MSI. When omitted it is read from
    src\Directory.Build.props (the single source of truth shared with the
    assembly version), so the MSI ProductVersion can never drift from the
    version the app reports at runtime. Must be x.y.z with each part <= 65535
    (MSI ProductVersion constraint).

.PARAMETER Configuration
    Build configuration passed to dotnet publish (default Release).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = Split-Path -Parent $here
$publish = Join-Path $here "publish"

# --- 0. Resolve version from Directory.Build.props unless overridden ----------
if (-not $Version) {
    $propsPath = Join-Path $repo "src\Directory.Build.props"
    if (-not (Test-Path $propsPath)) {
        throw "Cannot resolve version: '$propsPath' not found. Pass -Version explicitly."
    }
    $m = Select-String -Path $propsPath -Pattern '<Version>\s*([^<]+?)\s*</Version>' |
         Select-Object -First 1
    if (-not $m) { throw "No <Version> element found in '$propsPath'. Pass -Version explicitly." }
    $Version = $m.Matches[0].Groups[1].Value
    Write-Host "Version $Version (from src\Directory.Build.props)" -ForegroundColor DarkGray
}

Write-Host "=== LithicBackup installer build (v$Version) ===" -ForegroundColor Cyan

# --- 1. Publish GUI + Worker (self-contained, into one shared folder) ---------
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

$commonArgs = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:DebugType=none",       # no .pdb files in the shipped installer
    "-p:DebugSymbols=false",
    "-o", $publish
)

Write-Host "Publishing GUI..." -ForegroundColor Yellow
dotnet publish (Join-Path $repo "src\LithicBackup\LithicBackup.csproj") @commonArgs
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed." }

Write-Host "Publishing Worker service..." -ForegroundColor Yellow
dotnet publish (Join-Path $repo "src\LithicBackup.Worker\LithicBackup.Worker.csproj") @commonArgs
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

foreach ($exe in @("LithicBackup.exe", "LithicBackup.Worker.exe")) {
    if (-not (Test-Path (Join-Path $publish $exe))) { throw "Expected $exe missing from publish output." }
}

# --- 2. Ensure the WiX tool + required extensions are available ----------------
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "Installing WiX dotnet tool..." -ForegroundColor Yellow
    dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the wix tool." }
}

# Extensions must match the wix tool's version exactly.  Adding an extension
# without a version pulls the LATEST from NuGet (e.g. 7.x), which fails to load
# against a v5 tool with WIX6101 ("Could not find expected package root folder
# wixext5").  So discover the tool's own version and pin every extension to it.
$wixVer = (& wix --version 2>$null | Select-Object -First 1)
if ($wixVer) { $wixVer = ($wixVer -split '\+')[0].Trim() }   # strip build metadata

function Ensure-WixExtension {
    param([string] $Name)

    $exts   = (& wix extension list -g) 2>$null
    $wanted = if ($wixVer) { "$Name $wixVer" } else { $Name }
    if ($exts -match [regex]::Escape($wanted)) { return }   # correct version already present

    # A wrong-version copy would shadow the pinned add, so remove it first.
    if ($exts -match [regex]::Escape($Name)) {
        Write-Host "Removing mismatched $Name..." -ForegroundColor Yellow
        & wix extension remove -g $Name 2>$null | Out-Null
    }

    $spec = if ($wixVer) { "$Name/$wixVer" } else { $Name }
    Write-Host "Adding $spec..." -ForegroundColor Yellow
    & wix extension add -g $spec
    if ($LASTEXITCODE -ne 0) { throw "Failed to add $spec." }
}

# Required extensions:
#   * UI   — the classic install wizard (WixUI_InstallDir).
#   * Util — the WixShellExec custom action behind the "Launch Lithic Backup"
#            checkbox on the exit dialog (Wix4UtilCA_X64 / WixShellExec).
Ensure-WixExtension "WixToolset.UI.wixext"
Ensure-WixExtension "WixToolset.Util.wixext"

# --- 3. Build the managed custom action ---------------------------------------
# SignalLithicGuiShutdown (installer\CustomActions) asks a running GUI to close
# itself before InstallValidate, so an upgrade never hits the file-in-use dialog
# — without needing elevation to kill an elevated GUI.  Its targets run MakeSfxCA
# to emit LithicBackup.CustomActions.CA.dll, which Package.wxs embeds as a Binary.
$caProj = Join-Path $here "CustomActions\LithicBackup.CustomActions.csproj"
$caDll  = Join-Path $here "CustomActions\bin\Release\LithicBackup.CustomActions.CA.dll"
Write-Host "Building installer custom action..." -ForegroundColor Yellow
dotnet build $caProj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Custom action build failed." }
if (-not (Test-Path $caDll)) { throw "Custom action built but $caDll is missing." }

# --- 4. Compile the MSI -------------------------------------------------------
$msi = Join-Path $here ("LithicBackup-{0}-x64.msi" -f $Version)
if (Test-Path $msi) { Remove-Item -Force $msi }

Push-Location $here
try {
    wix build "Package.wxs" `
        -d "Version=$Version" `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        -arch x64 `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed." }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "MSI: $msi"
Write-Host ("     {0:N1} MB" -f ((Get-Item $msi).Length / 1MB))
