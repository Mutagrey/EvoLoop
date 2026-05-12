# Windows Bundles

This folder remains a committed generated-artifact area for GitHub-ready Windows binaries.

Current checked-in state: stale CLI-only snapshots. They contain `Agent.Cli.exe` and a CLI `agent.cmd` wrapper, but not the current `Agent.Tui.exe` or `agent-cli.cmd` layout.

Run `scripts\prepare-github-windows-bundles.cmd` or `scripts/prepare-github-windows-bundles.sh` after target changes and before distributing these folders.

Available targets:
- `win-x64`
- `win-arm64`

After refresh, each target folder should contain:
- `Agent.Tui.exe`
- `Agent.Cli.exe`
- `agent.cmd`
- `agent-cli.cmd`
- `config.json.example`

Expected usage after refresh from `cmd` or PowerShell inside the target folder:

```cmd
agent.cmd
agent-cli.cmd doctor
agent-cli.cmd run "inspect repository and summarize current issues"
```

The executable is self-contained and does not require a separately installed `.NET SDK`.
