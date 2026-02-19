# EvoLoop Agent CLI

Production-oriented autonomous coding agent CLI in pure C# (`.NET 6`) with no third-party libraries.

## Highlights

- ReAct loop (`analyze -> tool -> observe -> repeat`)
- Guarded autonomy with approval gates for risky actions
- Tooling for files, git, shell, lexical search, semantic-like reranking
- Non-streaming model API support (Qwen, DeepSeek, GLM profiles)
- Portable/no-admin friendly usage
- Telemetry hard-disabled for dotnet CLI child processes
- SQLite CLI event storage with JSONL fallback

## Project Layout

- `src/Agent.Cli`: CLI entrypoint, REPL, UX renderer, approvals
- `src/Agent.Core`: contracts, config, ReAct loop, policy
- `src/Agent.Tools`: tool implementations + search service
- `src/Agent.Providers`: model gateway adapters + routing
- `src/Agent.Storage`: event store implementations
- `tests/Agent.Tests`: lightweight test harness (no test framework dependency)

## Requirements

- .NET SDK 6.x
- `git` on PATH
- Optional: `rg` for fast lexical search
- Optional: `sqlite3` for event storage backend

## Configuration

Default config path: `~/.evoloop-agent/config.json`

Profiles are mapped as:

- `reasoning` -> DeepSeek
- `fast` -> Qwen
- `fallback` -> GLM

Set API key in env var named by `api.apiKeyEnvVar` (default `EVOLOOP_API_KEY`).

## Privacy Defaults

- `DOTNET_CLI_TELEMETRY_OPTOUT=1`
- `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`
- `DOTNET_NOLOGO=1`

These are enforced by the CLI process and propagated to spawned subprocesses.

## Build

```bash
HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build EvoLoopAgent.sln
```

## Run (Interactive)

```bash
HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project src/Agent.Cli
```

## Run (One-shot)

```bash
HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project src/Agent.Cli -- run "analyze git status and summarize" --profile reasoning
```

## Commands

- `/task <text>`
- `/status`
- `/tools`
- `/history`
- `/config`
- `/approve` and `/deny` (informational; approvals are inline)
- `/exit`

## Tests

```bash
HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project tests/Agent.Tests
```
