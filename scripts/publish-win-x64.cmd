@echo off
setlocal
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set SCRIPT_DIR=%~dp0
set REPO_ROOT=%SCRIPT_DIR%..
set OUTPUT_DIR=%REPO_ROOT%\artifacts\publish\win-x64
set LOCK_DIR=%REPO_ROOT%\artifacts\publish-locks
set DOTNET_CLI_HOME=%REPO_ROOT%\.tooling\home
set NUGET_PACKAGES=%REPO_ROOT%\.tooling\nuget

if not exist "%REPO_ROOT%\.tooling\home" mkdir "%REPO_ROOT%\.tooling\home" >nul 2>nul
if not exist "%REPO_ROOT%\.tooling\nuget" mkdir "%REPO_ROOT%\.tooling\nuget" >nul 2>nul
if not exist "%LOCK_DIR%" mkdir "%LOCK_DIR%" >nul 2>nul

if exist "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" (
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" publish "%REPO_ROOT%\src\Agent.Cli\Agent.Cli.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:NuGetLockFilePath="%LOCK_DIR%\Agent.Cli.win-x64.packages.lock.json" ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
  if errorlevel 1 exit /b 1
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" publish "%REPO_ROOT%\src\Agent.Tui\Agent.Tui.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:NuGetLockFilePath="%LOCK_DIR%\Agent.Tui.win-x64.packages.lock.json" ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
) else (
  dotnet publish "%REPO_ROOT%\src\Agent.Cli\Agent.Cli.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:NuGetLockFilePath="%LOCK_DIR%\Agent.Cli.win-x64.packages.lock.json" ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
  if errorlevel 1 exit /b 1
  dotnet publish "%REPO_ROOT%\src\Agent.Tui\Agent.Tui.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:NuGetLockFilePath="%LOCK_DIR%\Agent.Tui.win-x64.packages.lock.json" ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
)

call :writeWrappers "%OUTPUT_DIR%"
if errorlevel 1 exit /b 1

endlocal

exit /b 0

:writeWrappers
(
  echo @echo off
  echo setlocal
  echo set ROUTE=tui
  echo call :selectRoute %%*
  echo if /I "%%ROUTE%%"=="cli" ^(
  echo   "%%~dp0Agent.Cli.exe" --workspace "%%cd%%" %%*
  echo ^) else ^(
  echo   "%%~dp0Agent.Tui.exe" --workspace "%%cd%%" %%*
  echo ^)
  echo exit /b %%errorlevel%%
  echo.
  echo :selectRoute
  echo if "%%~1"=="" exit /b 0
  echo if /I "%%~1"=="--workspace" ^(
  echo   shift
  echo   shift
  echo   goto selectRoute
  echo ^)
  echo if /I "%%~1"=="--config" ^(
  echo   shift
  echo   shift
  echo   goto selectRoute
  echo ^)
  echo if /I "%%~1"=="--profile" ^(
  echo   shift
  echo   shift
  echo   goto selectRoute
  echo ^)
  echo if /I "%%~1"=="--model" ^(
  echo   shift
  echo   shift
  echo   goto selectRoute
  echo ^)
  echo if /I "%%~1"=="--no-color" ^(
  echo   shift
  echo   goto selectRoute
  echo ^)
  echo if /I "%%~1"=="--offline-strict" ^(
  echo   shift
  echo   goto selectRoute
  echo ^)
  echo if /I "%%~1"=="doctor" set ROUTE=cli
  echo if /I "%%~1"=="run" set ROUTE=cli
  echo if /I "%%~1"=="plan" set ROUTE=cli
  echo if /I "%%~1"=="review" set ROUTE=cli
  echo if /I "%%~1"=="repl" set ROUTE=cli
  echo if /I "%%~1"=="interactive" set ROUTE=cli
  echo exit /b 0
) > "%~1\agent.cmd"

(
  echo @echo off
  echo setlocal
  echo "%%~dp0Agent.Cli.exe" --workspace "%%cd%%" %%*
  echo endlocal
) > "%~1\agent-cli.cmd"

copy /Y "%REPO_ROOT%\scripts\windows-bundle-install-user-command.cmd" "%~1\install-user-command.cmd" >nul

exit /b 0
