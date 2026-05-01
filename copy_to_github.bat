@echo off
echo Copying LithicBackup project files to D:\github\lithicbackup...
echo.

robocopy "D:\visual studio projects\backup" "D:\github\lithicbackup" /MIR /XD .git .vs bin obj /XF *.user *.suo stdout.txt stderr.txt .orchestrator_history todo.txt todo2.txt todo3.txt /NFL /NDL /NJH /NP

if errorlevel 8 (
    echo.
    echo ERROR: Robocopy failed with error code %errorlevel%
    pause
    exit /b %errorlevel%
)

echo Copy complete.
echo.

