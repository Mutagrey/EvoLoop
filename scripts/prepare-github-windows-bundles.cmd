@echo off
setlocal

call "%~dp0publish-win-x64.cmd"
if errorlevel 1 exit /b 1

set REPO_ROOT=%~dp0..
set RELEASE_ROOT=%REPO_ROOT%\release\windows
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

if not exist "%RELEASE_ROOT%\win-x64" mkdir "%RELEASE_ROOT%\win-x64" >nul 2>nul

copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\Agent.Cli.exe" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\Agent.Tui.exe" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\*.pdb" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\agent.cmd" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\agent-cli.cmd" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\install-user-command.cmd" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\config\corporate.offline.config.json" "%RELEASE_ROOT%\win-x64\config.json.example" >nul

endlocal
