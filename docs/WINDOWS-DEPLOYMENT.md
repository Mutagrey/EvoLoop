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

The publish script produces a self-contained single-file executable layout suitable for copy-and-run distribution.

## Target Machine Expectations

- no admin rights required
- no package manager required
- no `.NET SDK` required
- optional network access only if model-backed execution is needed

## Recommended Bundle Contents

- published executable
- `config\corporate.offline.config.json` as a starting template
- project-local docs that matter operationally:
  - `README.md`
  - `docs/OPERATING-MODES.md`
  - `docs/STATUS.md`

## First Run

```powershell
.\EvoLoop.Agent.exe doctor
```

Use `doctor` before task execution to confirm:
- gateway reachability
- auth presence
- workspace writability
- optional tool availability
