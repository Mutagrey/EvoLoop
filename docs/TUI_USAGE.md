# TUI Usage

Start the minimal TUI shell:

```bash
dotnet run --project src/Agent.Tui --
```

Packaged Windows usage:

```cmd
agent.cmd
```

The shell shows workspace, profile, runtime mode, a readonly transcript, an input line, and a status bar.

## Commands

- `/help`: show available TUI commands and current limitations.
- `/exit`: close the TUI.

Normal text is recorded as a user message and returns `Agent integration pending`. Model-backed work still uses `Agent.Cli` for now:

```bash
dotnet run --project src/Agent.Cli -- run "task"
dotnet run --project src/Agent.Cli -- plan "task"
dotnet run --project src/Agent.Cli -- review
```

## Keyboard

- `Enter`: submit input.
- `Ctrl+C`: close the TUI.
- `Ctrl+D`: close the TUI when input is empty.

## Known Limitations

- No agent runtime integration yet.
- No streaming output, tool activity rendering, approval dialog, diff view, session list, or slash suggestion popup yet.
