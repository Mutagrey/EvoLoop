#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="$repo_root/artifacts/publish/win-x64"
local_dotnet="$repo_root/.tooling/dotnet8/dotnet"
local_home="$repo_root/.tooling/home"
local_nuget="$repo_root/.tooling/nuget"

export HOME="$local_home"
export DOTNET_CLI_HOME="$local_home"
export NUGET_PACKAGES="$local_nuget"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mkdir -p "$local_home" "$local_nuget"

dotnet_bin="dotnet"
if [[ -x "$local_dotnet" ]]; then
  dotnet_bin="$local_dotnet"
fi

"$dotnet_bin" publish "$repo_root/src/Agent.Cli/Agent.Cli.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$output_dir"
