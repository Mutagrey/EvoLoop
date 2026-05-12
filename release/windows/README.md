# Windows Bundles

This folder remains a committed generated-artifact area for GitHub-ready Windows binaries.

Run `scripts\prepare-github-windows-bundles.cmd` or `scripts/prepare-github-windows-bundles.sh` after target changes and before distributing these folders.

Available targets:
- `win-x64`

The target folder should contain:
- `Agent.Tui.exe`
- `Agent.Cli.exe`
- `agent.cmd`
- `agent-cli.cmd`
- `install-user-command.cmd`
- `config.json.example`

Expected usage after refresh from `cmd` inside the target folder:

```cmd
agent.cmd
agent-cli.cmd doctor
agent-cli.cmd run "inspect repository and summarize current issues"
install-user-command.cmd
```

The executable is self-contained and does not require a separately installed `.NET SDK`.
`install-user-command.cmd` updates only the current user's PATH through `HKCU\Environment`; it does not require admin rights or PowerShell.
