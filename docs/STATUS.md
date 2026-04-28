# Status

## Completed

- Centralized the target framework and common compiler settings in `Directory.Build.props`.
- Moved the solution baseline to `.NET 8`.
- Added runtime capability probing for shell, workspace storage, `git`, `rg`, `sqlite3`, and model gateway access.
- Added `doctor` mode to the CLI and REPL.
- Added explicit runtime modes: `full`, `offline-strict`, and `local-only degraded`.
- Blocked model-backed task execution cleanly when the gateway is unavailable instead of failing later with opaque runtime errors.
- Added no-op fallback for session persistence when workspace storage is unavailable.
- Made REPL command history tolerant of non-writable workspaces.
- Added explicit unavailable responses for git and shell tools when prerequisites are missing.
- Added plain-output fallback for unsupported Windows consoles to avoid broken ANSI rendering in `cmd`.
- Injected runtime capability context into the model loop so prompts respect no-admin/offline/missing-tool constraints.
- Tightened workspace memory ranking so noisy failed runs are less likely to pollute future context.
- Added Windows self-contained publish scripts and a VS Code publish task.
- Rewrote documentation around Windows-first, no-admin, low-dependency operation.

## Current Problems

- The current workspace does not have `.NET 8 SDK`, so this change set could not be compiled or tested end-to-end here.
- No packaged Windows smoke test has been executed yet against the new `win-x64` publish path.
- The agent still depends on a remote/local model gateway for actual autonomous task execution; `local-only degraded` mode is diagnostic-safe, not a replacement for a local model runtime.
- Search reranking still assumes model access and falls back implicitly rather than exposing a richer degraded UX.

## Next Improvements

- Run a real `.NET 8` build and execute the test harness on a matching build machine.
- Smoke-test the self-contained Windows artifact on a restricted non-admin machine.
- Add a richer local-only command surface that can perform repository diagnostics without invoking the model loop.
- Add tests for git/shell unavailable paths and non-writable workspace behavior.
- Remove leftover machine-specific clutter files from version control as part of a dedicated cleanup pass.
