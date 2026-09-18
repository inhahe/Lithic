@echo off
cd /d "%~dp0"

echo Building LithicBackup...
echo.

dotnet build src\LithicBackup\LithicBackup.csproj -c Release

if %errorlevel% neq 0 (
    echo.
    echo BUILD FAILED.
    pause
    exit /b %errorlevel%
)

echo.
echo Build succeeded.
echo.

REM --- Build the background Worker service ---
REM The service locks its DLLs while running, so stop it first (if running),
REM build, then restart it. Stopping/starting needs admin; if it's not
REM running or not installed, we just build.

set "WORKER_WAS_RUNNING="
sc query LithicBackup 2>nul | find "RUNNING" >nul
if %errorlevel% equ 0 (
    set "WORKER_WAS_RUNNING=1"
    echo Stopping LithicBackup Worker service...
    net stop LithicBackup
    if errorlevel 1 (
        echo.
        echo Could not stop the service ^(run this script as Administrator^).
        echo The Worker build may fail because its files are locked.
        echo.
    )
)

echo Building LithicBackup Worker...
echo.

dotnet build src\LithicBackup.Worker\LithicBackup.Worker.csproj -c Release

set "WORKER_BUILD_RESULT=%errorlevel%"

if defined WORKER_WAS_RUNNING (
    echo.
    echo Restarting LithicBackup Worker service...
    net start LithicBackup
)

if %WORKER_BUILD_RESULT% neq 0 (
    echo.
    echo WORKER BUILD FAILED.
    pause
    exit /b %WORKER_BUILD_RESULT%
)

echo.
echo Build succeeded.
echo.

REM --- Copy the freshly built executables into this folder for convenience ---
set "GUI_EXE=%~dp0src\LithicBackup\bin\Release\net8.0-windows\LithicBackup.exe"
set "WORKER_EXE=%~dp0src\LithicBackup.Worker\bin\Release\net8.0-windows\LithicBackup.Worker.exe"

copy /y "%GUI_EXE%" "%~dp0LithicBackup.exe" >nul
if errorlevel 1 echo WARNING: could not copy LithicBackup.exe here.
copy /y "%WORKER_EXE%" "%~dp0LithicBackup.Worker.exe" >nul
if errorlevel 1 echo WARNING: could not copy LithicBackup.Worker.exe here.

echo Executables:
echo   %~dp0LithicBackup.exe
echo   %~dp0LithicBackup.Worker.exe
echo.

call build-msi.bat