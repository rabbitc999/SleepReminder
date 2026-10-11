@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0manage-restart-policy.ps1" -Action status %*
echo.
pause
