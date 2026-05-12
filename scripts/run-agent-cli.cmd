@echo off
setlocal
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

if not exist "%~dp0..\.tooling" mkdir "%~dp0..\.tooling" >nul 2>nul
if not exist "%~dp0..\.tooling\home" mkdir "%~dp0..\.tooling\home" >nul 2>nul
if not exist "%~dp0..\.tooling\nuget" mkdir "%~dp0..\.tooling\nuget" >nul 2>nul

set DOTNET_CLI_HOME=%~dp0..\.tooling\home
set NUGET_PACKAGES=%~dp0..\.tooling\nuget

if exist "%~dp0..\.tooling\dotnet8\dotnet.exe" (
  call "%~dp0build-agent.cmd" >nul
  "%~dp0..\.tooling\dotnet8\dotnet.exe" run --project "%~dp0..\src\Agent.Cli" --no-build -- %*
) else (
  dotnet run --project "%~dp0..\src\Agent.Cli" -- %*
)

exit /b %errorlevel%
