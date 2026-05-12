# TUI Specification

## Summary

The main interactive terminal surface is the separate `Agent.Tui` executable. Pure command-line operation stays in the separate `Agent.Cli` executable.

Shared startup and agent wiring live in `Agent.Hosting`. Agent execution, policy, tools, providers, and storage remain in their existing layers.

## First Target

Current implementation target:

- split CLI and TUI into separate executable targets
- keep packaged `agent.cmd` as the TUI wrapper
- add packaged `agent-cli.cmd` as the pure CLI wrapper
- keep the old line REPL in `Agent.Cli`
- vendor the TUI dependency packages for later offline restore
- do not implement the full TUI shell yet

The current TUI executable is a placeholder that reports the workspace, profile, runtime mode, and available explicit commands.

## Dependency Policy

TUI dependencies must be restorable without nuget.org in normal repo restore. Packages live under `vendor/nuget`, and package versions are centralized in `Directory.Packages.props`.

Prepared dependency:

- `Terminal.Gui` `1.19.0`

`Terminal.Gui` `2.1.0` targets `net10.0` only and is not compatible with the current `net8.0` project. `Terminal.Gui` `2.0.0` supports `net8.0` but pulls a larger dependency graph, including Roslyn and logging packages, so it is not the first choice for this Windows-first low-dependency app.

## Runtime Shape

Expected commands:

```bash
dotnet run --project src/Agent.Tui --
dotnet run --project src/Agent.Cli -- doctor
dotnet run --project src/Agent.Cli -- run "task"
dotnet run --project src/Agent.Cli -- plan "task"
dotnet run --project src/Agent.Cli -- review
dotnet run --project src/Agent.Cli -- repl
```

Packaged Windows wrappers should map `agent.cmd` to `Agent.Tui.exe` and `agent-cli.cmd` to `Agent.Cli.exe`.

## Next Implementation Phase

Implement a minimal TUI shell behind focused CLI classes:

- app start and shutdown
- static chat screen
- input box
- status bar
- `/help`
- `/exit`

Do not connect the TUI to the agent runtime until the minimal shell is stable.
