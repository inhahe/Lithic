<#
.SYNOPSIS
    Build the whole LithicBackup app as ONE consistent self-contained set and
    deploy it over the installed copy in Program Files.

.DESCRIPTION
    The GUI (LithicBackup.exe) and the Worker service (LithicBackup.Worker.exe)
    share the same class libraries (Core / Infrastructure / Services). If you
    ever update those DLLs piecemeal - e.g. copy a fresh Infrastructure.dll to
    fix the Worker but leave the old GUI in place - the halves reference each
    other's types by name and you get a startup TypeLoadException
    ("Could not load type 'X' from assembly 'Y'").

    This script makes that impossible: it publishes BOTH executables into one
    shared folder (so every assembly comes from a single build), then mirrors
    that folder into the install directory. GUI + Worker + all libraries are
    always the exact same version.

    Because the MSI installs a SELF-CONTAINED build (the .NET runtime is bundled
    in the install folder), this script also publishes self-contained. That
    keeps the .exe host, runtimeconfig.json, deps.json and the runtime DLLs all
    mutually consistent - never mix a framework-dependent build into a
    self-contained install.

    Run it from an elevated (Administrator) PowerShell: it writes to
    Program Files and stops/starts the Windows service, both of which need
    admin rights.

.PARAMETER Configuration
    Build configuration passed to dotnet publish. Default Release (matches the
    MSI and build.bat). Pass -Configuration Debug for a debug deploy.

.PARAMETER InstallDir
    Target install folder. Default "C:\Program Files\Lithic Backup".

.PARAMETER ServiceName
    Windows service name to stop/start around the deploy. Default "Lithic Backup".

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Configuration Debug
#>
param(
    [string]$Configuration = "Release",
    [string]$InstallDir    = "C:\Program Files\Lithic Backup",
    [string]$ServiceName   = "Lithic Backup"
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host $msg -ForegroundColor Cyan }

# --- 0. Preconditions ---------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "deploy.ps1 must run elevated (it writes to Program Files and controls the " +
          "'$ServiceName' service). Re-run from an Administrator PowerShell."
}

if (-not (Test-Path $InstallDir)) {
    throw "Install folder '$InstallDir' does not exist. Do the FIRST install with the " +
          "MSI in .\installer\, then use this script for subsequent updates."
}

$here    = Split-Path -Parent $MyInvocation.MyCommand.Definition
$guiProj = Join-Path $here "src\LithicBackup\LithicBackup.csproj"
$wrkProj = Join-Path $here "src\LithicBackup.Worker\LithicBackup.Worker.csproj"
$publish = Join-Path $env:TEMP "lithic-deploy-publish"

Write-Host "=== LithicBackup deploy ($Configuration) ===" -ForegroundColor Green
Write-Host "  Install dir : $InstallDir"
Write-Host "  Service     : $ServiceName"
Write-Host ""

# --- 1. Publish GUI + Worker (self-contained) into ONE shared folder ----------
# Both projects publish into $publish so every shared library resolves to a
# single build - this is what guarantees version consistency.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

$commonArgs = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:DebugType=none",       # no .pdb files in the deployed folder
    "-p:DebugSymbols=false",
    "-o", $publish
)

Write-Step "Publishing GUI..."
dotnet publish $guiProj @commonArgs
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed." }

Write-Step "Publishing Worker service..."
dotnet publish $wrkProj @commonArgs
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

foreach ($exe in @("LithicBackup.exe", "LithicBackup.Worker.exe")) {
    if (-not (Test-Path (Join-Path $publish $exe))) {
        throw "Expected $exe missing from publish output - build produced an unexpected layout."
    }
}

# --- 2. Free the install folder: stop the service, close the GUI --------------
# The Worker service locks its DLLs while running; the GUI locks LithicBackup.dll.
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$svcWasRunning = $false
if ($svc -and $svc.Status -eq 'Running') {
    $svcWasRunning = $true
    Write-Step "Stopping service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
}

$guiProcs = Get-Process -Name LithicBackup -ErrorAction SilentlyContinue
if ($guiProcs) {
    Write-Step "Closing running GUI (LithicBackup.exe) so its DLLs unlock..."
    $guiProcs | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# --- 3. Copy the consistent set into the install folder -----------------------
# Overwrite in place; don't delete unrelated files that may live alongside.
Write-Step "Deploying to '$InstallDir'..."
Copy-Item -Path (Join-Path $publish '*') -Destination $InstallDir -Recurse -Force

# --- 4. Restart the service if we stopped it ----------------------------------
if ($svcWasRunning) {
    Write-Step "Restarting service '$ServiceName'..."
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
}

# --- 5. Report ----------------------------------------------------------------
Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Green
foreach ($f in @("LithicBackup.exe", "LithicBackup.dll", "LithicBackup.Worker.exe",
                 "LithicBackup.Core.dll", "LithicBackup.Infrastructure.dll",
                 "LithicBackup.Services.dll")) {
    $p = Join-Path $InstallDir $f
    if (Test-Path $p) {
        $item = Get-Item $p
        Write-Host ("  {0,-32} {1}" -f $f, $item.LastWriteTime)
    }
}
Write-Host ""
Write-Host "GUI + Worker + libraries are now one consistent build." -ForegroundColor Green
