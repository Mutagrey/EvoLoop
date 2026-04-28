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

## Coding Rules

- Minimize code before optimizing it.
- Remove duplication before adding abstraction.
- Fail with explicit reasons, not generic “command failed” errors.
- Treat missing environment capabilities as expected input, not exceptional edge cases.
- Keep optional dependencies optional with clear fallbacks.
- Do not couple docs or code to machine-specific absolute paths.

## Change Checklist

- Keep the project runnable in `local-only degraded` mode.
- Preserve path safety and approval safety behavior.
- Update `docs/STATUS.md` when architecture or operating behavior changes.
- Update `docs/TESTING.md` when build, publish, or validation flow changes.
- Prefer one canonical explanation per topic; link instead of restating.
