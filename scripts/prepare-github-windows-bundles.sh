#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
release_root="$repo_root/release/windows"

"$repo_root/scripts/publish-win-x64.sh"

mkdir -p "$release_root/win-x64"
cp "$repo_root/artifacts/publish/win-x64/Agent.Cli.exe" "$release_root/win-x64/"
cp "$repo_root/artifacts/publish/win-x64/Agent.Tui.exe" "$release_root/win-x64/"
cp "$repo_root/artifacts/publish/win-x64/"*.pdb "$release_root/win-x64/"
cp "$repo_root/config/corporate.offline.config.json" "$release_root/win-x64/config.json.example"

cat > "$release_root/win-x64/agent.cmd" <<'EOF'
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
cat > "$release_root/win-x64/agent-cli.cmd" <<'EOF'
@echo off
setlocal
"%~dp0Agent.Cli.exe" --workspace "%cd%" %*
endlocal
EOF
cp "$repo_root/scripts/windows-bundle-install-user-command.cmd" "$release_root/win-x64/install-user-command.cmd"
