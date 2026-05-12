@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0field-test-agent.ps1" %*
exit /b %errorlevel%
