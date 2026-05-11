# TUI Specification

## Summary

The main interactive terminal surface is `agent` with no subcommand. Explicit command-line modes remain available through `agent run`, `agent plan`, `agent review`, `agent doctor`, and `agent repl`.

The TUI is owned by the CLI layer. Agent execution, policy, tools, providers, and storage remain in their existing layers.

## First Target

Current implementation target:

- prepare the CLI/TUI split without a second executable
- make bare `agent` enter the TUI path
- keep the old line REPL available as `agent repl`
- vendor the TUI dependency packages for later offline restore
- do not implement the full TUI shell yet

The current TUI path is a placeholder that reports the workspace, profile, runtime mode, and available explicit commands.

## Dependency Policy

TUI dependencies must be restorable without nuget.org in normal repo restore. Packages live under `vendor/nuget`, and package versions are centralized in `Directory.Packages.props`.

Prepared dependency:

- `Terminal.Gui` `1.19.0`

`Terminal.Gui` `2.1.0` targets `net10.0` only and is not compatible with the current `net8.0` project. `Terminal.Gui` `2.0.0` supports `net8.0` but pulls a larger dependency graph, including Roslyn and logging packages, so it is not the first choice for this Windows-first low-dependency app.

## Runtime Shape

Expected commands:

```bash
agent
agent tui
agent repl
agent doctor
agent run "task"
agent plan "task"
agent review
```

`agent tui` is kept as an explicit alias for the default TUI path.

## Next Implementation Phase

Implement a minimal TUI shell behind focused CLI classes:

- app start and shutdown
- static chat screen
- input box
- status bar
- `/help`
- `/exit`

Do not connect the TUI to the agent runtime until the minimal shell is stable.
