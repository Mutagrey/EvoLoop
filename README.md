# EvoLoop Agent CLI

Production-oriented autonomous coding agent CLI in pure C# (`.NET 9`) with no third-party libraries.

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

Recommended profile tuning:

- `reasoning` (DeepSeek): best for multi-step code changes, planning, and refactor tasks.
  - `temperature: 0.15`
  - `maxTokens: 1800`
- `fast` (Qwen): best for quick checks, small edits, and summaries.
  - `temperature: 0.10`
  - `maxTokens: 900`
- `fallback` (GLM): backup profile with balanced behavior.
  - `temperature: 0.20`
  - `maxTokens: 1200`

Runtime safety boundaries (recommended):

- `runtime.modelMinOutputTokens: 256`
- `runtime.modelMaxOutputTokens: 4096`
- `runtime.modelMinTemperature: 0.0`
- `runtime.modelMaxTemperature: 0.7`

Auth options (either one):

- Option A: env var named by `api.apiKeyEnvVar` (default `EVOLOOP_API_KEY`)
- Option B: put token directly in `api.apiKey` in your config file

Examples:

macOS/Linux (bash/zsh):

```bash
export EVOLOOP_API_KEY="your_token_here"
```

Windows PowerShell:

```powershell
$env:EVOLOOP_API_KEY="your_token_here"
```

Windows CMD:

```cmd
set EVOLOOP_API_KEY=your_token_here
```

Config file example (without env var):

```json
{
  "api": {
    "apiKey": "your_token_here"
  }
}
```

`.env` file is optional. The agent does not require `.env` unless you personally use a loader/workflow for it.

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

## VS Code Terminal Integration

This repo includes ready-to-use tasks at:

- `/Users/Shared/Dev/SmartGlucoProject/EvoLoop/.vscode/tasks.json`

How to use:

1. Open project in VS Code.
2. Open `Terminal` -> `Run Task...`.
3. Run one of:
   - `Agent: REPL`
   - `Agent: Run Task`
   - `Agent: Run Task (Offline Strict)`
   - `Agent: Build`
   - `Agent: Tests`

For auth, set token in your terminal before running tasks:

```bash
export EVOLOOP_API_KEY="your_token_here"
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

## Gateway 404 Troubleshooting

If you get `gateway failed (404) {"detail":"not found"}`, this is usually endpoint path mismatch, not model name.

- `max_tokens` is already sent by the client.
- Check provider/profile mapping:
  - `provider: "openai"` -> uses `api.openAiCompatiblePath`
  - `provider: "custom"` -> uses `api.customPath`
- Path join rule:
  - If `baseUrl` already contains a path prefix (for example `/v1`), use relative path without leading slash (`chat/completions`).
  - If path starts with `/`, it is treated from host root.

Example combinations:

- `baseUrl: "https://host"` + `openAiCompatiblePath: "/v1/chat/completions"` -> `https://host/v1/chat/completions`
- `baseUrl: "https://host/v1"` + `openAiCompatiblePath: "chat/completions"` -> `https://host/v1/chat/completions`

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
