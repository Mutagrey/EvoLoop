# Windows Deployment

## Primary Delivery Model

Ship EvoLoop as a **self-contained `win-x64` bundle** built on a developer or CI machine. Do not require `.NET SDK` installation on the target Windows machine.

## Build Machine Requirements

- `.NET 8 SDK`
- access to the repository
- optional access to the model gateway for validation

## Publish

```cmd
scripts\publish-win-x64.cmd
```

Output:
- `artifacts\publish\win-x64\`

The publish script produces a self-contained layout with separate TUI and CLI executables suitable for copy-and-run distribution.

If you want GitHub to contain committed ready-to-run Windows files, use:

```cmd
scripts\prepare-github-windows-bundles.cmd
```

That refreshes:
- `release\windows\win-x64\`

`release\windows` remains a committed generated-artifact area for now so GitHub can carry a ready-to-run offline Windows x64 bundle.

## Target Machine Expectations

- no admin rights required
- no package manager required
- no `.NET SDK` required
- optional network access only if model-backed execution is needed

## Recommended Bundle Contents

- `Agent.Tui.exe` with `agent.cmd`
- `Agent.Cli.exe` with `agent-cli.cmd`
- `install-user-command.cmd` for user-level PATH installation through `HKCU\Environment`
- `config\corporate.offline.config.json` as a starting template
- project-local docs that matter operationally:
  - `README.md`
  - `docs/OPERATING-MODES.md`
  - `docs/STATUS.md`

## First Run

```cmd
.\agent.cmd
.\agent-cli.cmd doctor
.\install-user-command.cmd
```

`install-user-command.cmd` is plain `cmd`; it does not require PowerShell, `ExecutionPolicy Bypass`, admin rights, or a target-machine `.NET SDK`.

Use `doctor` before task execution to confirm:
- gateway reachability
- auth presence
- workspace writability
- optional tool availability
