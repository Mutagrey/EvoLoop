@echo off
setlocal

set REPO_ROOT=%~dp0..
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_CLI_HOME=%REPO_ROOT%\.tooling\home
set NUGET_PACKAGES=%REPO_ROOT%\.tooling\nuget

if not exist "%REPO_ROOT%\.tooling\home" mkdir "%REPO_ROOT%\.tooling\home" >nul 2>nul
if not exist "%REPO_ROOT%\.tooling\nuget" mkdir "%REPO_ROOT%\.tooling\nuget" >nul 2>nul

if exist "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" (
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" build "%REPO_ROOT%\EvoLoopAgent.sln" --disable-build-servers -v minimal -nr:false /m:1
) else (
  dotnet build "%REPO_ROOT%\EvoLoopAgent.sln"
)

endlocal
