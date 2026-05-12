# TUI Usage

Start the minimal TUI shell:

```bash
dotnet run --project src/Agent.Tui --
```

Packaged Windows usage:

```cmd
agent.cmd
```

Compatibility commands such as `agent.cmd doctor`, `agent.cmd run`, `agent.cmd plan`, `agent.cmd review`, and `agent.cmd repl` route to the CLI target. Use `agent-cli.cmd` when you want the CLI explicitly.

The shell shows workspace, profile, runtime mode, a readonly transcript, an input line, and a status bar.

## Theme

Default theme:

```bash
dotnet run --project src/Agent.Tui -- --theme claude-dark
```

Available themes:

- `claude-dark`: dark terminal palette with gray text and amber/yellow workspace path.
- `mono`: conservative no-color style for limited terminals.

`--no-color` forces `mono`.

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
