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

The shell shows workspace, model profile, runtime mode, a readonly transcript, an input line, and a status bar.

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
- `/model`: show active model, provider, gateway, auth, and tool-calling mode.
- `/models`: list configured model profiles and fallback order.
- `/skills`: list workspace skills from `.evoloop/skills/**/SKILL.md`.
- `/config`: show grouped settings: connection, model profiles, safety, tool calling, limits, prompts, and storage.
- `/config path`: show loaded and default config paths.
- `/config open`: open the loaded config file with `$VISUAL`, `$EDITOR`, or Windows `notepad.exe`.
- `/config reload`: reload config and rebuild the runtime host.
- `/plan <task>`: run read-only plan mode.
- `/review [focus]`: review current workspace changes.
- `/diff`: show the current file from the latest review diff.
- `/diff files`: list navigable files from the latest review diff.
- `/diff next`, `/diff prev`, `/diff <number>`: navigate latest review diff files.
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

- Approval requests use a basic blocking approve/reject dialog; default `AutoEdit` skips prompts for writes and patches but still prompts for destructive actions.
- No session list or slash suggestion popup yet.
