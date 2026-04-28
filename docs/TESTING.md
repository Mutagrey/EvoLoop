# Testing

## Build Machine Validation

Expected environment:
- `.NET 8 SDK`

Commands:

```bash
dotnet build EvoLoopAgent.sln
dotnet run --project tests/Agent.Tests/Agent.Tests.csproj
dotnet run --project src/Agent.Cli -- doctor
dotnet run --project src/Agent.Cli -- plan "inspect architecture gaps"
dotnet run --project src/Agent.Cli -- review
```

## Windows Packaging Validation

Build:

```cmd
scripts\publish-win-x64.cmd
```

Smoke test on target machine:

```powershell
.\EvoLoop.Agent.exe doctor
.\EvoLoop.Agent.exe
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
- `review` mode without `git` -> review falls back to `workspace_snapshot_diff`
- `fs_patch` -> built-in patch apply works without `git`
- `workspace_undo` -> most recent file mutation is reversible from snapshot storage
- protected paths like `.env` or `.git/config` -> mutation denied by policy/path safety
- typed JSONL event log -> session/model/tool/approval/final events are persisted under `.evoloop/storage/events.jsonl`

## Notes

- This repository intentionally uses a lightweight custom test harness instead of an external test framework.
- If the local machine only has `.NET 6` or no compatible SDK, treat that as an environment blocker rather than a repository success signal.
- In the current macOS workspace, the bundled `.NET 8` SDK can compile the library projects, but executable-project builds may hang; validate CLI/tests again on a second machine before treating the change as fully verified.
