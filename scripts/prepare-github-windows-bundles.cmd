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
) else (
  echo Local .NET 8 SDK not found in .tooling\dotnet8.>&2
  exit /b 1
)

if not exist "%RELEASE_ROOT%\win-x64" mkdir "%RELEASE_ROOT%\win-x64" >nul 2>nul
if not exist "%RELEASE_ROOT%\win-arm64" mkdir "%RELEASE_ROOT%\win-arm64" >nul 2>nul

copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\Agent.Cli.exe" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-x64\*.pdb" "%RELEASE_ROOT%\win-x64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-arm64\Agent.Cli.exe" "%RELEASE_ROOT%\win-arm64\" >nul
copy /Y "%REPO_ROOT%\artifacts\publish\win-arm64\*.pdb" "%RELEASE_ROOT%\win-arm64\" >nul
copy /Y "%REPO_ROOT%\config\corporate.offline.config.json" "%RELEASE_ROOT%\win-x64\config.json.example" >nul
copy /Y "%REPO_ROOT%\config\corporate.offline.config.json" "%RELEASE_ROOT%\win-arm64\config.json.example" >nul

endlocal
