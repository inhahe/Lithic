@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem Publish a GitHub release for the current LithicBackup version, with the
rem matching MSI attached.
rem
rem   * Version is read automatically from src\Directory.Build.props, so the
rem     release tag can never drift from the assembly / MSI version.
rem   * The release is created on inhahe/Lithic and tagged  v<version>  (the
rem     exact form the in-app update check looks for).
rem   * The MSI  installer\LithicBackup-<version>-x64.msi  is uploaded as the
rem     release asset. Run build-msi.bat first if it doesn't exist yet.
rem
rem Requires the GitHub CLI (gh) to be installed and authenticated
rem (run  gh auth login  once). Pushing your local commits is a SEPARATE step
rem you do yourself; do that before releasing so the tag points at the right
rem commit on GitHub.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0"
set "REPO=inhahe/Lithic"

rem --- Read <Version> from src\Directory.Build.props -------------------------
set "VER="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Select-String -Path '%ROOT%src\Directory.Build.props' -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value"`) do set "VER=%%V"

if not defined VER (
    echo Could not read ^<Version^> from src\Directory.Build.props.
    exit /b 1
)

echo Version: %VER%
set "TAG=v%VER%"

rem --- Locate the matching MSI (prefer installer\, fall back to root) --------
set "MSI=%ROOT%installer\LithicBackup-%VER%-x64.msi"
if not exist "%MSI%" set "MSI=%ROOT%LithicBackup-%VER%-x64.msi"
if not exist "%MSI%" (
    echo.
    echo MSI for %VER% not found:
    echo     %ROOT%installer\LithicBackup-%VER%-x64.msi
    echo Build it first with  build-msi.bat  ^(or installer\build-installer.ps1^).
    exit /b 1
)

echo MSI:     %MSI%
echo Repo:    %REPO%
echo Tag:     %TAG%
echo.

rem --- Make sure gh is available --------------------------------------------
where gh >nul 2>&1
if errorlevel 1 (
    echo GitHub CLI ^(gh^) not found on PATH. Install it from https://cli.github.com/
    exit /b 1
)

rem --- Refuse to clobber an existing release for this tag -------------------
gh release view "%TAG%" --repo "%REPO%" >nul 2>&1
if not errorlevel 1 (
    echo.
    echo A release tagged %TAG% already exists on %REPO%.
    echo Bump the version ^(src\Directory.Build.props^) or delete that release first.
    exit /b 1
)

echo Creating release %TAG% on %REPO% ...
gh release create "%TAG%" "%MSI%" --repo "%REPO%" --title "%TAG%" --generate-notes
if errorlevel 1 (
    echo.
    echo Release creation FAILED. ^(Is gh authenticated?  gh auth login^)
    exit /b 1
)

echo.
echo Published %TAG% with LithicBackup-%VER%-x64.msi attached.
endlocal
