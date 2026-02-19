#!/usr/bin/env bash
set -euo pipefail

export HOME="${HOME:-/tmp}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/tmp}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet run --project "$(dirname "$0")/../src/Agent.Cli" -- "$@"
