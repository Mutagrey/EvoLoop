# Windows Bundles

This folder is intended to be committed to GitHub with ready-to-run Windows binaries.
Run `scripts\prepare-github-windows-bundles.cmd` or `scripts/prepare-github-windows-bundles.sh` after target changes to refresh the checked-in binaries.

Available targets:
- `win-x64`
- `win-arm64`

After refresh, each target folder contains:
- `Agent.Tui.exe`
- `Agent.Cli.exe`
- `agent.cmd`
- `agent-cli.cmd`
- `config.json.example`

Usage from `cmd` or PowerShell inside the target folder:

```cmd
agent.cmd
agent-cli.cmd doctor
agent-cli.cmd run "inspect repository and summarize current issues"
```

The executable is self-contained and does not require a separately installed `.NET SDK`.
