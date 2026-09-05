@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem Build a self-contained LithicBackup MSI and drop it in THIS folder.
rem
rem The heavy lifting is delegated to installer\build-installer.ps1, which
rem publishes the GUI + Worker as one self-contained win-x64 bundle (no .NET
rem runtime needed on the target machine) and compiles Package.wxs into an MSI
rem with the WiX Toolset. This wrapper then copies the finished installer here
rem so the .msi lands in the current directory. Version is read automatically
rem from src\Directory.Build.props.
rem
rem After building, every OTHER LithicBackup-*-x64.msi (older build) in both
rem installer\ and this folder is DELETED, leaving only the just-built MSI in
rem view. This stops the wrong (stale) installer from being launched by accident
rem when several versions have accumulated.
rem
rem Deleting rather than keeping them is safe because an MSI is not source: every
rem released version's <Version> is stamped in a committed src\Directory.Build.props,
rem so any earlier installer can be reproduced with
rem     git checkout <commit> && installer\build-installer.ps1
rem (the same build, though not the same bytes - an MSI carries a fresh package
rem code and timestamps each time). This script used to MOVE them to
rem installer\archive\ instead, which quietly accumulated 57 installers / 3.2 GB
rem that nobody had asked for and nobody was going to install.
rem
rem The one workflow that wants an older MSI is testing an UPGRADE (install the
rem previous version, then upgrade to this one, to exercise the shutdown
rem handshake in installer\CustomActions). Rebuild the older version from its
rem commit for that, rather than keeping every build on the off chance.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0"

echo Building self-contained MSI...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%installer\build-installer.ps1"
if errorlevel 1 (
    echo.
    echo MSI build FAILED.
    exit /b 1
)

rem Pick the just-built MSI (newest by date) from installer\ and copy it here.
set "MSI="
for /f "delims=" %%F in ('dir /b /a-d /o-d "%ROOT%installer\LithicBackup-*-x64.msi" 2^>nul') do (
    if not defined MSI set "MSI=%%F"
)
if not defined MSI (
    echo.
    echo Build reported success but no MSI was found in "%ROOT%installer".
    exit /b 1
)

copy /y "%ROOT%installer\%MSI%" "%ROOT%%MSI%" >nul
if errorlevel 1 (
    echo.
    echo Failed to copy "%MSI%" to "%ROOT%".
    exit /b 1
)

rem Delete every OTHER MSI (older build) so only the just-built %MSI% remains in
rem installer\ and in this folder. Prevents accidentally launching a stale
rem installer when many versions have piled up. See the note at the top of this
rem file for why these are safe to delete rather than keep.
set "REMOVED=0"
for /f "delims=" %%F in ('dir /b /a-d "%ROOT%installer\LithicBackup-*-x64.msi" 2^>nul') do (
    if /i not "%%F"=="%MSI%" (
        del /f /q "%ROOT%installer\%%F" >nul 2>&1 && set /a REMOVED+=1
    )
)
for /f "delims=" %%F in ('dir /b /a-d "%ROOT%LithicBackup-*-x64.msi" 2^>nul') do (
    if /i not "%%F"=="%MSI%" (
        del /f /q "%ROOT%%%F" >nul 2>&1 && set /a REMOVED+=1
    )
)

rem Sweep away the old archive\ folder this script used to fill, so upgrading
rem past this change doesn't silently leave gigabytes of it behind. rd only
rem succeeds on an empty directory, so anything the user deliberately put there
rem is left alone.
if exist "%ROOT%installer\archive" (
    del /f /q "%ROOT%installer\archive\LithicBackup-*-x64.msi" >nul 2>&1
    rd "%ROOT%installer\archive" >nul 2>&1
)

echo.
echo Created "%ROOT%%MSI%"
if not "%REMOVED%"=="0" echo Deleted %REMOVED% older MSI file(s) (rebuild any of them from git if ever needed).
endlocal
