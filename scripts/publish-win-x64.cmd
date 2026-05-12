@echo off
setlocal
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set SCRIPT_DIR=%~dp0
set REPO_ROOT=%SCRIPT_DIR%..
set OUTPUT_DIR=%REPO_ROOT%\artifacts\publish\win-x64
set DOTNET_CLI_HOME=%REPO_ROOT%\.tooling\home
set NUGET_PACKAGES=%REPO_ROOT%\.tooling\nuget

if not exist "%REPO_ROOT%\.tooling\home" mkdir "%REPO_ROOT%\.tooling\home" >nul 2>nul
if not exist "%REPO_ROOT%\.tooling\nuget" mkdir "%REPO_ROOT%\.tooling\nuget" >nul 2>nul

if exist "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" (
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" publish "%REPO_ROOT%\src\Agent.Cli\Agent.Cli.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
  if errorlevel 1 exit /b 1
  "%REPO_ROOT%\.tooling\dotnet8\dotnet.exe" publish "%REPO_ROOT%\src\Agent.Tui\Agent.Tui.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
) else (
  dotnet publish "%REPO_ROOT%\src\Agent.Cli\Agent.Cli.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
  if errorlevel 1 exit /b 1
  dotnet publish "%REPO_ROOT%\src\Agent.Tui\Agent.Tui.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUTPUT_DIR%"
)

endlocal
