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

set "TARGET_PROJECT=%~dp0..\src\Agent.Tui"
set "ORIGINAL_ARGS=%*"
call :selectTarget %*

if exist "%~dp0..\.tooling\dotnet8\dotnet.exe" (
  call "%~dp0build-agent.cmd" >nul
  "%~dp0..\.tooling\dotnet8\dotnet.exe" run --project "%TARGET_PROJECT%" --no-build -- %ORIGINAL_ARGS%
) else (
  dotnet run --project "%TARGET_PROJECT%" -- %ORIGINAL_ARGS%
)

exit /b %errorlevel%

:selectTarget
if "%~1"=="" exit /b 0
if /I "%~1"=="--workspace" (
  shift
  shift
  goto selectTarget
)
if /I "%~1"=="--config" (
  shift
  shift
  goto selectTarget
)
if /I "%~1"=="--profile" (
  shift
  shift
  goto selectTarget
)
if /I "%~1"=="--model" (
  shift
  shift
  goto selectTarget
)
if /I "%~1"=="--no-color" (
  shift
  goto selectTarget
)
if /I "%~1"=="--offline-strict" (
  shift
  goto selectTarget
)
if /I "%~1"=="doctor" set "TARGET_PROJECT=%~dp0..\src\Agent.Cli"
if /I "%~1"=="run" set "TARGET_PROJECT=%~dp0..\src\Agent.Cli"
if /I "%~1"=="plan" set "TARGET_PROJECT=%~dp0..\src\Agent.Cli"
if /I "%~1"=="review" set "TARGET_PROJECT=%~dp0..\src\Agent.Cli"
if /I "%~1"=="repl" set "TARGET_PROJECT=%~dp0..\src\Agent.Cli"
if /I "%~1"=="interactive" set "TARGET_PROJECT=%~dp0..\src\Agent.Cli"
exit /b 0
