# Testing

## Build Machine Validation

Expected environment:
- `.NET 8 SDK`

Commands:

```bash
dotnet build EvoLoopAgent.sln
dotnet run --project tests/Agent.Tests/Agent.Tests.csproj
dotnet run --project src/Agent.Cli -- doctor
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

## Notes

- This repository intentionally uses a lightweight custom test harness instead of an external test framework.
- If the local machine only has `.NET 6` or no compatible SDK, treat that as an environment blocker rather than a repository success signal.
