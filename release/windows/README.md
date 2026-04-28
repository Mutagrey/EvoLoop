# Windows Bundles

This folder is intended to be committed to GitHub with ready-to-run Windows binaries.

Available targets:
- `win-x64`
- `win-arm64`

Each target folder contains:
- `Agent.Cli.exe`
- `agent.cmd`
- `config.json.example`

Usage from `cmd` or PowerShell inside the target folder:

```cmd
agent.cmd doctor
agent.cmd run "inspect repository and summarize current issues"
```

The executable is self-contained and does not require a separately installed `.NET SDK`.
