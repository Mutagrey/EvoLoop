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
- `/model`: open a model-profile picker for the current TUI session.
- `/model <profile>`: switch model profile for the current TUI session without changing config.
- `/model status`: show active model, provider, gateway, auth, and tool-calling mode.
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

Normal text runs through the shared agent runtime in `run` mode. Model-backed `run` and `plan` still require a configured model gateway; `review` can use the local degraded fallback when model execution is unavailable. Long command output such as `/config` opens at the start of the new result; use transcript scrolling to continue through it.

```bash
dotnet run --project src/Agent.Tui --
```

## Keyboard

- `Enter`: submit input.
- `Tab`: complete the selected slash-command suggestion.
- `Up` / `Down`: move through visible slash-command suggestions when the input starts with `/`.
- `Up` / `Down`, `PageUp` / `PageDown`, `Home` / `End`: navigate active picker menus.
- `PageUp` / `PageDown`: scroll the transcript.
- `Home` / `End`: jump transcript to top or bottom.
- Mouse wheel: scroll the transcript when supported by the terminal.
- `Esc`: cancel the current running task; when a picker is open, close the picker.
- `Ctrl+C`: close the TUI.
- `Ctrl+D`: close the TUI when input is empty.

## Known Limitations

- `/config` is still a rendered settings summary, not a full settings editor.
- No session list yet.

## Migration Notes

- `/model` now opens the picker. Use `/model status` for the previous model-status text output.
