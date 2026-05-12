#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
local_dotnet="$repo_root/.tooling/dotnet8/dotnet"
local_home="$repo_root/.tooling/home"

export DOTNET_CLI_HOME="$local_home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mkdir -p "$local_home" "$repo_root/.tooling/nuget"
export NUGET_PACKAGES="$repo_root/.tooling/nuget"

"$repo_root/scripts/build-agent.sh" >/dev/null

if [[ -x "$local_dotnet" ]]; then
  "$local_dotnet" run --project "$repo_root/src/Agent.Cli" --no-build -- "$@"
else
  dotnet run --project "$repo_root/src/Agent.Cli" -- "$@"
fi
