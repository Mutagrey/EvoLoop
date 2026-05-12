# TUI Specification

This is the canonical current TUI behavior spec. Usage details live in [TUI_USAGE.md](TUI_USAGE.md), architecture boundaries live in [ARCHITECTURE.md](ARCHITECTURE.md), and historical planning text in [EvoLoop_TUI_SPEC.md](EvoLoop_TUI_SPEC.md) is not a source of truth for current behavior.

## Summary

The main interactive terminal surface is the separate `Agent.Tui` executable. Pure command-line operation stays in the separate `Agent.Cli` executable.

Shared startup and agent wiring live in `Agent.Hosting`. Agent execution, policy, tools, providers, and storage remain in their existing layers.

## Current Target

Implemented:

- split CLI and TUI into separate executable targets
- keep packaged `agent.cmd` as the TUI wrapper
- add packaged `agent-cli.cmd` as the pure CLI wrapper
- keep the old line REPL in `Agent.Cli`
- vendor the TUI dependency packages for offline restore
- Terminal.Gui shell with transcript, input line, status bar, `/help`, `/status`, `/plan`, `/review`, and `/exit`
- theme layer with `claude-dark` and `mono`
- TUI-local runtime observer and approval adapter connected to the shared `Agent.Hosting` runtime path
- basic Terminal.Gui approve/reject dialog for runtime approval requests
- compact runtime event formatting for model, tool, approval, memory, and session status lines

Normal text runs through the shared agent runtime in `run` mode. `/plan <task>` runs in read-only plan mode. `/review [focus]` runs review mode and can use the local degraded review fallback when model execution is unavailable.

## Dependency Policy

TUI dependencies must be restorable without nuget.org in normal repo restore. Packages live under `vendor/nuget`, and package versions are centralized in `Directory.Packages.props`.

Active dependency:

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

## Current TUI Shell

The minimal shell supports:

- app start and shutdown
- chat transcript
- input box
- status bar
- `/help`
- `/status`
- `/plan <task>`
- `/review [focus]`
- `/exit`
- `Ctrl+C` shutdown
- `Ctrl+D` shutdown when input is empty
- `--theme claude-dark|mono`
- `--no-color` to force mono styling

Default styling is a dark terminal palette: black background, gray text, muted chrome, and amber/yellow workspace path. Theme definitions live in the TUI layer so future palettes can be added without changing Core, Tools, Providers, or Storage.

## Next Implementation Phase

Add richer tool activity rendering without changing Core, Tools, Providers, or Storage ownership.
