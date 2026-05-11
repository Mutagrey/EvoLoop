#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
release_root="$repo_root/release/windows"

"$repo_root/scripts/publish-win-x64.sh"

if [[ -x "$repo_root/.tooling/dotnet8/dotnet" ]]; then
  HOME="$repo_root/.tooling/home" \
  DOTNET_CLI_HOME="$repo_root/.tooling/home" \
  NUGET_PACKAGES="$repo_root/.tooling/nuget" \
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DOTNET_NOLOGO=1 \
  "$repo_root/.tooling/dotnet8/dotnet" publish "$repo_root/src/Agent.Cli/Agent.Cli.csproj" \
    -c Release \
    -r win-arm64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    --disable-build-servers -nr:false /m:1 \
    -o "$repo_root/artifacts/publish/win-arm64"
else
  echo "Local .NET 8 SDK not found in .tooling/dotnet8; cannot prepare tracked Windows bundles." >&2
  exit 1
fi

mkdir -p "$release_root/win-x64" "$release_root/win-arm64"
cp "$repo_root/artifacts/publish/win-x64/Agent.Cli.exe" "$release_root/win-x64/"
cp "$repo_root/artifacts/publish/win-x64/"*.pdb "$release_root/win-x64/"
cp "$repo_root/artifacts/publish/win-arm64/Agent.Cli.exe" "$release_root/win-arm64/"
cp "$repo_root/artifacts/publish/win-arm64/"*.pdb "$release_root/win-arm64/"
cp "$repo_root/config/corporate.offline.config.json" "$release_root/win-x64/config.json.example"
cp "$repo_root/config/corporate.offline.config.json" "$release_root/win-arm64/config.json.example"

for target in win-x64 win-arm64; do
  cat > "$release_root/$target/agent.cmd" <<'EOF'
@echo off
setlocal
"%~dp0Agent.Cli.exe" --workspace "%cd%" %*
endlocal
EOF
done
