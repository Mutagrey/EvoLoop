#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
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

if [[ -x "$local_dotnet" ]]; then
  "$local_dotnet" run --project "$repo_root/tests/Agent.Tests/Agent.Tests.csproj" --no-build
else
  dotnet run --project "$repo_root/tests/Agent.Tests/Agent.Tests.csproj"
fi
