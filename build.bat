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
echo Executable:
echo   %~dp0src\LithicBackup\bin\Release\net8.0-windows\LithicBackup.exe
echo.
