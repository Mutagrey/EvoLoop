# EvoLoop Agent CLI

Windows-first autonomous coding agent CLI in pure C# with no third-party runtime dependencies.

The project is designed for restricted environments:
- no admin rights on the target machine
- no dependency installation on the target machine
- limited or unavailable internet access
- explicit degraded behavior instead of opaque crashes

## Operating Model

Primary delivery mode:
- self-contained Windows package built on a developer/build machine

Secondary delivery mode:
- source run on a machine that already has `.NET 8 SDK`

Runtime modes:
- `full`: gateway reachable, normal model-backed execution
- `offline-strict`: same as `full`, but network shell commands stay constrained by policy
- `local-only degraded`: CLI starts, diagnostics/config/tools remain usable, but model-backed agent tasks are blocked with a clear message

Console behavior:
- ANSI-capable terminals use colored status output and live step spinner
- plain Windows `cmd` falls back to plain ASCII output without ANSI escape noise

Details:
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- [docs/OPERATING-MODES.md](docs/OPERATING-MODES.md)
- [docs/WINDOWS-DEPLOYMENT.md](docs/WINDOWS-DEPLOYMENT.md)

## Repository Layout

- `src/Agent.Cli`: CLI entrypoint, REPL, diagnostics, startup mode selection
- `src/Agent.Core`: contracts, orchestration, policy, capability model
- `src/Agent.Tools`: file, git, shell, and search tools
- `src/Agent.Providers`: model gateway clients
- `src/Agent.Storage`: event and memory persistence with fallback behavior
- `tests/Agent.Tests`: lightweight test harness without external test framework
- `config/corporate.offline.config.json`: offline-strict example config
- `docs/`: architecture, deployment, testing, and project status

## Quick Start

Packaged Windows usage:

```powershell
.\agent.cmd doctor
.\agent.cmd run "inspect repository and summarize current issues"
```

Source usage on a build/developer machine:

```bash
dotnet run --project src/Agent.Cli -- doctor
dotnet run --project src/Agent.Cli -- run "inspect repository and summarize current issues"
```

If `.tooling/dotnet8` exists, the helper scripts automatically prefer that local SDK instead of a system-wide install.

## Build And Publish

Developer build:

```bash
./scripts/build-agent.sh
./scripts/test-agent.sh
```

Windows developer build:

```cmd
scripts\build-agent.cmd
scripts\test-agent.cmd
```

Windows self-contained publish:

```cmd
scripts\publish-win-x64.cmd
```

Or from Unix/macOS build hosts:

```bash
./scripts/publish-win-x64.sh
```

Published output goes to `artifacts/publish/win-x64/`.

Tracked GitHub-ready Windows bundles:

```bash
./scripts/prepare-github-windows-bundles.sh
```

or on Windows:

```cmd
scripts\prepare-github-windows-bundles.cmd
```

This refreshes the committed bundles in:
- `release/windows/win-x64/`
- `release/windows/win-arm64/`

## Commands

CLI:
- `agent doctor`
- `agent run "<task>"`
- `agent plan "<task>"`
- `agent review`
- `agent --offline-strict`

REPL:
- `/task <text>`
- `/plan <text>`
- `/review [focus]`
- `/doctor`
- `/status`
- `/tools`
- `/history`
- `/memory`
- `/undo`
- `/cmdlog`
- `/config`
- `/exit`

## Configuration

Default config path:
- `%USERPROFILE%\.evoloop-agent\config.json` on Windows
- `~/.evoloop-agent/config.json` on Unix-like systems

Example offline-strict config:
- [config/corporate.offline.config.json](config/corporate.offline.config.json)

The CLI still supports:
- `EVOLOOP_API_KEY`
- `api.apiKey`
- custom auth headers in config

## Development Rules

Repository work rules live in:
- [AGENTS.md](AGENTS.md)

Current implementation status lives in:
- [docs/STATUS.md](docs/STATUS.md)
