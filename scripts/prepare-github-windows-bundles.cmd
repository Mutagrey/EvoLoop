@echo off
setlocal

call "%~dp0publish-win-x64.cmd"
if errorlevel 1 exit /b 1

set REPO_ROOT=%~dp0..
set RELEASE_ROOT=%REPO_ROOT%\release\windows
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_CLI_HOME=%REPO_ROOT%\.tooling\home
set NUGET_PACKAGES=%REPO_ROOT%\.tooling\nuget

if exist "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" (
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" publish "%REPO_ROOT%\src\Agent.Cli\Agent.Cli.csproj" ^
    -c Release ^
    -r win-arm64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    --disable-build-servers -nr:false /m:1 ^
    -o "%REPO_ROOT%\artifacts\publish\win-arm64"
  if errorlevel 1 exit /b 1
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" publish "%REPO_ROOT%\src\Agent.Tui\Agent.Tui.csproj" ^
    -c Release ^
    -r win-arm64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    --disable-build-servers -nr:false /m:1 ^
    -o "%REPO_ROOT%\artifacts\publish\win-arm64"
) else (
  echo Local .NET 8 SDK not found in .tooling\dotnet8.>&2
  exit /b 1
)

if not exist "%RELEASE_ROOT%\win-x64" mkdir "%RELEASE_ROOT%\win-x64" >nul 2>nul
if not exist "%RELEASE_ROOT%\win-arm64" mkdir "%RELEASE_ROOT%\win-arm64" >nul 2>nul

copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\Agent.Cli.exe" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\Agent.Tui.exe" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\*.pdb" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-arm64\Agent.Cli.exe" "%RELEASE_ROOT%\win-arm64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-arm64\Agent.Tui.exe" "%RELEASE_ROOT%\win-arm64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-arm64\*.pdb" "%RELEASE_ROOT%\win-arm64\" >nul
copy /Y "%REPO_ROOT%\config\corporate.offline.config.json" "%RELEASE_ROOT%\win-x64\config.json.example" >nul
copy /Y "%REPO_ROOT%\config\corporate.offline.config.json" "%RELEASE_ROOT%\win-arm64\config.json.example" >nul

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
) > "%RELEASE_ROOT%\win-x64\agent.cmd"

(
  echo @echo off
  echo setlocal
  echo "%%~dp0Agent.Cli.exe" --workspace "%%cd%%" %%*
  echo endlocal
) > "%RELEASE_ROOT%\win-x64\agent-cli.cmd"

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
) > "%RELEASE_ROOT%\win-arm64\agent.cmd"

(
  echo @echo off
  echo setlocal
  echo "%%~dp0Agent.Cli.exe" --workspace "%%cd%%" %%*
  echo endlocal
) > "%RELEASE_ROOT%\win-arm64\agent-cli.cmd"

endlocal
