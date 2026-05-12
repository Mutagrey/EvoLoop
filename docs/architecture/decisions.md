# Architecture Decisions

This file records decisions made during the architecture preparation audit. It should stay short and link to source-of-truth docs instead of duplicating full behavior.

## ADR-0001: Documentation-First Cleanup

Status: accepted

Decision:

- Create architecture audit documents before production refactors.
- Do not add Pi-inspired features in the first cleanup step.
- Do not rewrite the project or add dependencies.

Reason:

- The codebase already has partial layering, but responsibilities are still mixed in several hotspots.
- A written map reduces the risk of large accidental behavior changes.

## ADR-0002: Preserve Existing Compatibility Surfaces

Status: accepted

Decision:

- Preserve public CLI commands, tool names, config shape, project references, storage locations, and default JSON-ReAct behavior during cleanup.
- Any breaking change needs an explicit migration note and docs update.

Reason:

- EvoLoop targets restricted Windows machines where predictable packaged behavior matters more than internal elegance.

## ADR-0003: Keep JSONL Canonical

Status: accepted

Decision:

- JSONL remains the canonical event/session log.
- Sqlite remains optional projection/cache when `sqlite3` is available.

Reason:

- JSONL works in restricted/offline environments without extra dependencies.
- Optional sqlite must not become a runtime prerequisite.

## ADR-0004: UI Must Not Own Runtime Logic

Status: accepted

Decision:

- CLI and TUI render state and collect input.
- Shared command/use-case behavior belongs in Hosting/Application.
- Runtime owns agent step execution, policy, approval flow, and tool orchestration.
- TUI must connect through runtime events and approval interfaces, not by importing CLI internals.

Reason:

- The current CLI contains useful behavior that TUI will need, but copying it into TUI would duplicate runtime policy and fallback logic.

## ADR-0005: Providers Only Adapt Model Protocols

Status: accepted

Decision:

- `Agent.Providers` owns HTTP payloads, provider-specific response extraction, native tool-call parsing, streaming accumulation, and fallback parsing entry points.
- Providers must not know about workspace paths, approvals, shell policy, or UI.

Reason:

- Corporate gateways vary in model protocol support. Keeping provider logic isolated makes fallback behavior testable and replaceable.

## ADR-0006: Shell Remains Fallback-Only

Status: accepted

Decision:

- `exec_shell` remains a fallback-only tool.
- Specialized tools should be preferred for file, search, git, patch, and snapshot operations.
- Shell commands stay policy- and approval-controlled.

Reason:

- Shell is the highest-risk operation, especially on restricted Windows machines.

## ADR-0007: Do Not Introduce New Assemblies Yet

Status: accepted

Decision:

- Initial cleanup should reorganize files and responsibilities inside existing projects.
- New assemblies or broad folder moves should wait until concrete coupling requires them.

Reason:

- Existing project boundaries are already mostly aligned with the target architecture.
- The immediate problems are oversized files, duplicate helpers, and mixed responsibilities.

## ADR-0008: TUI Integration Comes After Runtime Event Cleanup

Status: accepted

Decision:

- Keep the current minimal TUI shell pending integration.
- Connect TUI to agent execution only after application/runtime boundaries and structured event data are stable.

Reason:

- `SpinnerObserver` currently derives activity by parsing text. TUI should consume structured events instead of depending on CLI renderer behavior.
