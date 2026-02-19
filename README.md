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

- .NET SDK 9.x
- `git` on PATH
- Optional: `rg` for fast lexical search
- Optional: `sqlite3` for event storage backend

## Configuration

Default config path: `~/.evoloop-agent/config.json`

Corporate-safe template in repo:

- `/Users/Shared/Dev/SmartGlucoProject/EvoLoop/config/corporate.offline.config.json`

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

## Run With Offline Strict Mode

`--offline-strict` blocks network shell commands by policy.  
Only model gateway hosts (`api.baseUrl` host + `safety.allowedNetworkHosts`) are permitted, and still require approval.

```bash
HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project src/Agent.Cli -- run "review repository and propose cleanup" --profile reasoning --offline-strict
```

Use the corporate template directly:

```bash
HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project src/Agent.Cli -- run "review repository and propose cleanup" --profile reasoning --config /Users/Shared/Dev/SmartGlucoProject/EvoLoop/config/corporate.offline.config.json
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

## Add Remote And Push

HTTPS remote:

```bash
git remote add origin https://github.com/<your-org-or-user>/<your-repo>.git
git branch -M main
git add .
git commit -m "Initial commit: EvoLoop Agent CLI MVP"
git push -u origin main
```

SSH remote:

```bash
git remote add origin git@github.com:<your-org-or-user>/<your-repo>.git
git branch -M main
git add .
git commit -m "Initial commit: EvoLoop Agent CLI MVP"
git push -u origin main
```

If `origin` already exists:

```bash
git remote set-url origin <new-url>
git push -u origin main
```
