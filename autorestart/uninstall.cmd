@echo off
setlocal
echo Removing SleepReminder auto-restart policy...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-restart-policy.ps1" %*
echo.
pause
