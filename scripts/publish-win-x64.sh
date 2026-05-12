#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="$repo_root/artifacts/publish/win-x64"
lock_dir="$repo_root/artifacts/publish-locks"
local_dotnet="$repo_root/.tooling/dotnet8/dotnet"
local_home="$repo_root/.tooling/home"
local_nuget="$repo_root/.tooling/nuget"

export HOME="$local_home"
export DOTNET_CLI_HOME="$local_home"
export NUGET_PACKAGES="$local_nuget"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mkdir -p "$local_home" "$local_nuget" "$lock_dir"

dotnet_bin="dotnet"
if [[ -x "$local_dotnet" ]]; then
  dotnet_bin="$local_dotnet"
fi

"$dotnet_bin" publish "$repo_root/src/Agent.Cli/Agent.Cli.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:NuGetLockFilePath="$lock_dir/Agent.Cli.win-x64.packages.lock.json" \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$output_dir"

"$dotnet_bin" publish "$repo_root/src/Agent.Tui/Agent.Tui.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:NuGetLockFilePath="$lock_dir/Agent.Tui.win-x64.packages.lock.json" \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$output_dir"

cat > "$output_dir/agent.cmd" <<'EOF'
@echo off
setlocal
set ROUTE=tui
call :selectRoute %*
if /I "%ROUTE%"=="cli" (
  "%~dp0Agent.Cli.exe" --workspace "%cd%" %*
) else (
  "%~dp0Agent.Tui.exe" --workspace "%cd%" %*
)
exit /b %errorlevel%

:selectRoute
if "%~1"=="" exit /b 0
if /I "%~1"=="--workspace" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--config" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--profile" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--model" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--no-color" (
  shift
  goto selectRoute
)
if /I "%~1"=="--offline-strict" (
  shift
  goto selectRoute
)
if /I "%~1"=="doctor" set ROUTE=cli
if /I "%~1"=="run" set ROUTE=cli
if /I "%~1"=="plan" set ROUTE=cli
if /I "%~1"=="review" set ROUTE=cli
if /I "%~1"=="repl" set ROUTE=cli
if /I "%~1"=="interactive" set ROUTE=cli
exit /b 0
EOF

cat > "$output_dir/agent-cli.cmd" <<'EOF'
@echo off
setlocal
"%~dp0Agent.Cli.exe" --workspace "%cd%" %*
endlocal
EOF

cp "$repo_root/scripts/windows-bundle-install-user-command.cmd" "$output_dir/install-user-command.cmd"
