# EvoLoop Agent CLI

Production-oriented autonomous coding agent CLI in pure C# (`.NET 9`) with no third-party libraries.

## Highlights

- ReAct loop (`analyze -> tool -> observe -> repeat`)
- Guarded autonomy with approval gates for risky actions
- Workspace boundary hardening (path traversal and sibling-prefix bypass protection)
- Destructive shell patterns blocked by default (`rm -rf`, `git reset --hard`, etc.)
- Tooling for files, git, shell, lexical search, semantic-like reranking
- Live step feed (`PLAN` / `RUN` / `RESULT`) and post-run activity panel (`Edited` / `Explored` / `Ran`)
- Pseudographic console panels with wrapped output and aligned status lines
- Deterministic recovery layer (auto-repair of common tool args + bootstrap tool calls when model format/behavior degrades)
- Persistent workspace memory across restarts + automatic context compaction
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
  - `temperature: 0.12`
  - `maxTokens: 2200`
- `fast` (Qwen): best for quick checks, small edits, and summaries.
  - `temperature: 0.05`
  - `maxTokens: 1000`
- `fallback` (GLM): backup profile with balanced behavior.
  - `temperature: 0.18`
  - `maxTokens: 1600`

Runtime safety boundaries (recommended):

- `runtime.modelMinOutputTokens: 256`
- `runtime.modelMaxOutputTokens: 4096`
- `runtime.modelMinTemperature: 0.0`
- `runtime.modelMaxTemperature: 0.7`
- `runtime.maxInvalidModelResponses: 6`
- `runtime.maxConsecutiveFinalWithoutTools: 5`
- `runtime.invalidResponsesBeforeProfileSwitch: 2`
- `runtime.finalWithoutToolsBeforeProfileSwitch: 2`

Model output reliability controls:

- `api.preferJsonResponseFormat: true`
- `api.responseFormatFallbackWithoutJson: true`
- `api.systemPromptMode: "user"` (`system` | `user` | `both`)
- `api.systemPromptFallbackToUserMessage: true`
- `runtime.adaptivePromptingEnabled: true` (dynamic format/strategy tightening after model failures)

Persistent memory and automatic context compression:

- `runtime.memoryEnabled: true`
- `runtime.memoryMaxRuns: 24`
- `runtime.memoryContextMaxChars: 7000`
- `runtime.historyMaxMessages: 80`
- `runtime.historyMaxChars: 120000`
- `runtime.historyKeepTailMessages: 18`
- `runtime.observationMaxChars: 6000`

How it works:

- memory is stored locally in `.evoloop/storage/memory-runs.jsonl` (no remote telemetry)
- on startup, agent injects relevant snippets from previous runs into model context
- when context grows too large, old turns are compacted into structured summary automatically
- adaptive prompt layer tightens output contract after format failures (self-correction loop)

The agent will request JSON-formatted output from the model and fallback automatically if gateway does not support `response_format`.
Default mode is `user` because many gateways ignore/deprioritize `system`. If gateway supports strict system role well, you can switch to `system` or `both`.

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

If your existing config still uses `system`, force recommended `user` mode:

```json
{
  "api": {
    "systemPromptMode": "user",
    "systemPromptFallbackToUserMessage": true
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
- `/memory`
- `/config`
- `/approve` and `/deny` (informational; approvals are inline)
- `/exit`

During run, CLI now shows:

- `STEP`: current loop step
- `MEMORY`: memory load/update and context compaction notifications
- `PLAN`: concise next action in human language (no raw JSON/args dump)
- `RUN`: exact action being executed
- `RESULT`: tool outcome in human-readable form
- nested tree-style indentation for related events (`PLAN -> RUN -> RESULT/APPROVAL`)
- `Activity` panel after run: `Edited` / `Explored` / `Ran` timeline (with `+/-` when available via `git diff --numstat`)

If you see repeated `MODEL` cycles without useful tool actions, watch for `WARN` lines:

- `Model response format invalid ...`
- `Final rejected ... task requires tool actions first`
- `Tool '...' missing required arguments: ...` (agent validates required tool schema before execution and forces model to retry)
- `MODEL-SWITCH ...` (agent automatically switched profile, e.g. `reasoning -> fallback -> fast`)
- `RECOVER ...` (agent recovered a valid tool decision from malformed/plain-text output)

Reliability hardening now includes:

- deterministic bootstrap tool call when model returns `final` too early for action tasks
- parser support for additional tool-call shapes (`tool_calls`, `function_call`, `name+arguments`, nested response objects)
- automatic safe argument repair for common fields (`path`, `query`, `pathspec`, `ref`, `command`, `message`) based on task text and recent tool observations
- deterministic fallback actions when required args are still missing (for example: switch to `fs_list` / `search_lexical` to discover paths first)
- plain-text final fallback for non-tool tasks (prevents wasting useful model answers on strict JSON mismatch)

The run now stops with a clear message once retry limits are hit (instead of looping silently).
Unknown tool loops are treated as invalid model decisions and are now auto-stopped/profile-switched by the same guardrails.
Tool argument parsing is tolerant to common variants (`filePath`, nested `arguments`, plain-text `input`, bullet/YAML-style fields) to reduce `missing required argument` failures.

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
