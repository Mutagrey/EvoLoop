# AGENTS

## Mission

Keep EvoLoop small, predictable, and operable on restricted Windows machines.

## Hard Constraints

- Design for Windows first.
- Assume target machines may have no admin rights.
- Assume target machines may have no internet access.
- Assume target machines may not have `.NET SDK` or optional tools installed.
- Prefer self-contained delivery over install-time setup.
- Prefer built-in .NET functionality over extra tooling or packages.

## Architecture Rules

- `docs/ARCHITECTURE.md` is the source of truth for boundaries and runtime behavior.
- `docs/OPERATING-MODES.md` is the source of truth for mode semantics.
- `docs/STATUS.md` is the only progress ledger.
- `README.md` stays short and points to deeper docs instead of duplicating them.
- Keep one canonical explanation per topic. If a detail already lives in a source-of-truth doc, link to it instead of restating it.
- Public CLI commands, tool names, config shape, and project references are compatibility surfaces. Change them only with an explicit migration note.
- New runtime behavior must state which layer owns it: CLI, Core, Tools, Providers, or Storage.

## Coding Rules

- Minimize code before optimizing it.
- Remove duplication before adding abstraction.
- Fail with explicit reasons, not generic “command failed” errors.
- Treat missing environment capabilities as expected input, not exceptional edge cases.
- Keep optional dependencies optional with clear fallbacks.
- Do not couple docs or code to machine-specific absolute paths.
- Keep `Program.cs` thin. Startup wiring belongs there; REPL/session behavior belongs in focused CLI classes.
- Keep provider code limited to request/response adaptation. Policy, workspace paths, and tool execution stay outside `Agent.Providers`.
- Prefer internal helpers and small files over large mixed-responsibility classes.
- Prefer specialized tools over shell. `exec_shell` remains fallback-only and policy-controlled.
- Do not add third-party packages unless there is no practical built-in .NET alternative.
- Tests use the lightweight in-repo harness; do not add an external test framework without changing `docs/TESTING.md`.

## Change Checklist

- Keep the project runnable in `local-only degraded` mode.
- Preserve path safety and approval safety behavior.
- Update `docs/STATUS.md` when architecture or operating behavior changes.
- Update `docs/TESTING.md` when build, publish, or validation flow changes.
- Prefer one canonical explanation per topic; link instead of restating.
- For refactors, preserve public CLI/tool/config behavior unless the task explicitly asks for a breaking change.
- Add or adjust tests when changing path safety, policy, fallback behavior, or operating modes.
