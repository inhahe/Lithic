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
rem After building, every OTHER LithicBackup-*-x64.msi (older builds) in both
rem installer\ and this folder is moved into installer\archive\, leaving only
rem the just-built MSI in view. This stops the wrong (stale) installer from
rem being launched by accident when several versions have accumulated.
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

rem Archive every OTHER MSI (older builds) so only the just-built %MSI% remains
rem in installer\ and in this folder. Prevents accidentally launching a stale
rem installer when many versions have piled up.
set "ARCHIVE=%ROOT%installer\archive"
if not exist "%ARCHIVE%" mkdir "%ARCHIVE%"

set "MOVED=0"
for /f "delims=" %%F in ('dir /b /a-d "%ROOT%installer\LithicBackup-*-x64.msi" 2^>nul') do (
    if /i not "%%F"=="%MSI%" (
        move /y "%ROOT%installer\%%F" "%ARCHIVE%\" >nul && set /a MOVED+=1
    )
)
for /f "delims=" %%F in ('dir /b /a-d "%ROOT%LithicBackup-*-x64.msi" 2^>nul') do (
    if /i not "%%F"=="%MSI%" (
        move /y "%ROOT%%%F" "%ARCHIVE%\" >nul && set /a MOVED+=1
    )
)

echo.
echo Created "%ROOT%%MSI%"
if not "%MOVED%"=="0" echo Archived %MOVED% older MSI file(s) to "%ARCHIVE%".
endlocal
