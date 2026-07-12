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
    Product version stamped into the MSI (default 1.0.0). Must be x.y.z with
    each part <= 65535 (MSI ProductVersion constraint).

.PARAMETER Configuration
    Build configuration passed to dotnet publish (default Release).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = Split-Path -Parent $here
$publish = Join-Path $here "publish"

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

# --- 2. Ensure the WiX tool + UI extension are available -----------------------
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "Installing WiX dotnet tool..." -ForegroundColor Yellow
    dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the wix tool." }
}

# Add the UI extension globally if it isn't already registered.
$exts = (& wix extension list -g) 2>$null
if ($exts -notmatch "WixToolset.UI.wixext") {
    Write-Host "Adding WiX UI extension..." -ForegroundColor Yellow
    wix extension add -g WixToolset.UI.wixext
    if ($LASTEXITCODE -ne 0) { throw "Failed to add WixToolset.UI.wixext." }
}

# --- 3. Compile the MSI -------------------------------------------------------
$msi = Join-Path $here ("LithicBackup-{0}-x64.msi" -f $Version)
if (Test-Path $msi) { Remove-Item -Force $msi }

Push-Location $here
try {
    wix build "Package.wxs" `
        -d "Version=$Version" `
        -ext WixToolset.UI.wixext `
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
Write-Host ("Size: {0:N1} MB" -f ((Get-Item $msi).Length / 1MB))
