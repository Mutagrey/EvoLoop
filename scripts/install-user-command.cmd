@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-user-command.ps1"
endlocal
