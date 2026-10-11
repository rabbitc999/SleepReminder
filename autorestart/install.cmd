@echo off
setlocal
echo Installing SleepReminder auto-restart policy...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-restart-policy.ps1" %*
echo.
pause
