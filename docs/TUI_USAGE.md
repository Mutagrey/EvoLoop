# TUI Usage

Start the TUI shell:

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

- `/help`: show available TUI commands.
- `/status`: show the last task status.
- `/plan <task>`: run read-only plan mode.
- `/review [focus]`: review current workspace changes.
- `/exit`: close the TUI.

Normal text runs through the shared agent runtime in `run` mode. Model-backed `run` and `plan` still require a configured model gateway; `review` can use the local degraded fallback when model execution is unavailable.

```bash
dotnet run --project src/Agent.Tui --
```

## Keyboard

- `Enter`: submit input.
- `Ctrl+C`: close the TUI.
- `Ctrl+D`: close the TUI when input is empty.

## Known Limitations

- Approval requests use a basic blocking approve/reject dialog.
- No diff view, session list, or slash suggestion popup yet.
