# Testing

## Build Machine Validation

Expected environment:
- `.NET 8 SDK`

Commands:

```bash
dotnet build EvoLoopAgent.sln
dotnet run --project tests/Agent.Tests/Agent.Tests.csproj
dotnet run --project src/Agent.Tui --
dotnet run --project src/Agent.Cli -- doctor
dotnet run --project src/Agent.Cli -- plan "inspect architecture gaps"
dotnet run --project src/Agent.Cli -- review
```

If `dotnet run --project tests/Agent.Tests/Agent.Tests.csproj` hangs while spawning MSBuild child nodes, use the two-step path:

```bash
dotnet build EvoLoopAgent.sln --no-restore
dotnet tests/Agent.Tests/bin/Debug/net8.0/Agent.Tests.dll
```

The lightweight harness also accepts name filters for focused runs:

```bash
dotnet tests/Agent.Tests/bin/Debug/net8.0/Agent.Tests.dll TUI
```

## Field Test Harness

Use the field-test harness for controlled real-agent runs against a sandbox workspace:

```bash
scripts/field-test-agent.sh
```

Windows:

```cmd
scripts\field-test-agent.cmd
```

The harness writes results under `artifacts/field-tests/<timestamp>/`, including command logs, stdout/stderr, storage size snapshots, line counts, and git diffs after each case. It covers read/search, plan, review, small edit, undo, path-safety denial, failed tool handling, approval rejection, and the fake-model bad-output regression case.

## Windows Packaging Validation

Build:

```cmd
scripts\publish-win-x64.cmd
```

Smoke test on target machine:

```cmd
.\agent.cmd
.\agent-cli.cmd doctor
.\agent-cli.cmd run "inspect repository and summarize current issues"
```

## Required Scenarios

- gateway reachable -> `full` mode or `offline-strict` mode, depending on config
- gateway unreachable -> `local-only degraded`
- missing `rg` -> lexical search fallback still works
- missing `sqlite3` -> event storage falls back to JSONL
- non-writable workspace -> CLI starts, but persistence is disabled clearly
- missing `git` -> git tools fail explicitly, not with unhandled process errors
- `plan` mode -> mutating tools and `exec_shell` are denied
- `review` mode with `git` -> review uses git evidence without mutating workspace
- `review` mode without `git` -> review falls back to `workspace_snapshot_diff`, including multi-file snapshot history
- `fs_patch` -> built-in patch apply works without `git`
- `workspace_undo` -> most recent file mutation is reversible from snapshot storage
- protected paths like `.env` or `.git/config` -> mutation denied by policy/path safety
- symlink traversal -> writes and patches through symlinked directories cannot escape the workspace
- fallback scanner -> skips `.git`, `.evoloop/storage`, `bin`, `obj`, `artifacts`, and binary files while tolerating inaccessible paths
- typed JSONL event log -> session/model/tool/approval/final events are persisted under `.evoloop/storage/events.jsonl`
- TUI session/storage inspection -> `/sessions`, `/session <id>`, `/storage`, and `/memory` read local JSONL/memory state
- TUI storage maintenance -> `/storage archive` rotates session/event/step JSONL and `/storage prune --keep N` keeps recent related records after an archive copy
- TUI context compaction -> `/compact` writes a `context_summary` event and memory entry when no task is running
- native non-streaming tools -> OpenAI-compatible `choices[].message.tool_calls` normalize to `ToolCallBlock`, execute locally, and append `role=tool` results
- native streaming tools -> fragmented `choices[].delta.tool_calls[].function.arguments` reconstruct into valid JSON arguments
- JSON-ReAct fallback -> strict JSON tool/final objects execute through the same policy and tool executor
- plain-text recovery -> `Action:` and `Arguments:` output is used only as a last-resort parser
- failed tools -> next turn receives a structured `ToolResultMessage` with `IsError=true`
- skills index -> `.evoloop/skills/*/SKILL.md` contributes only name/description/path until a tool reads the full file
- `Agent.Tui` -> starts the minimal Terminal.Gui shell; `/help`, `/exit`, `Ctrl+C`, and empty-input `Ctrl+D` close or respond as expected
- `Agent.Tui --theme claude-dark` -> uses the default dark theme with amber/yellow workspace path; `--no-color` forces mono styling
- `Agent.Cli repl` -> enters the legacy line-based REPL
- vendored TUI packages -> package files exist under `vendor/nuget`; `Agent.Tui` restores `Terminal.Gui` from the local feed

## Notes

- This repository intentionally uses a lightweight custom test harness instead of an external test framework.
- If the local machine only has `.NET 6` or no compatible SDK, treat that as an environment blocker rather than a repository success signal.
- In the current macOS workspace, `dotnet run --project tests/Agent.Tests/Agent.Tests.csproj` may hang before harness output. A direct `dotnet tests/Agent.Tests/bin/Debug/net8.0/Agent.Tests.dll` run after `dotnet build` is the current local workaround.
