# Architecture

## Summary

EvoLoop is a model-backed coding agent CLI with explicit fallback behavior for restricted environments. The architecture is organized around one central rule: **environment capability detection happens before agent execution**.

## Components

- `Agent.Cli`
  Startup, CLI parsing, REPL, doctor output, capability probing, degraded-mode gating.
- `Agent.Core`
  Contracts, policy, ReAct loop, tool context, runtime capability model.
- `Agent.Tools`
  File, git, shell, and search tools. Tools must return explicit unavailable states when required capabilities are missing.
- `Agent.Providers`
  Model gateway access only. No policy or workspace logic belongs here.
- `Agent.Storage`
  Session and memory persistence. JSONL is the safe fallback when optional storage tooling is absent.

## Startup Flow

1. Resolve workspace root.
2. Load effective config.
3. Probe runtime capabilities.
4. Choose operating mode.
5. Build tool/search/provider stack with capability-aware fallbacks.
6. Allow or block model-backed task execution based on the detected mode.

The runtime capability summary is also injected into the model loop as explicit context so the agent does not silently assume admin rights, package installation, internet reachability, or optional tool availability.

## Capability Model

The runtime probe detects:
- shell availability
- workspace writability
- `git`
- `rg`
- `sqlite3`
- model gateway configuration
- model gateway reachability
- auth presence

These capabilities drive both:
- user-facing diagnostics via `doctor`
- runtime behavior such as no-op persistence, scanner fallback, or blocked task execution
- model planning context, so tool decisions respect the actual machine constraints

## Operating Contract

- If gateway/model access is available, the agent can execute model-backed tasks.
- If gateway/model access is unavailable, the CLI still starts in `local-only degraded` mode.
- If workspace storage is unavailable, the CLI uses non-persistent fallbacks instead of crashing.
- If `rg` is unavailable, lexical search uses the built-in scanner.
- If `sqlite3` is unavailable, event storage uses JSONL.

## Boundaries

- CLI decides whether the run may start.
- Core decides how the agent loop behaves.
- Tools do not guess capability state; they read it from `ToolContext`.
- Providers do not know about workspace policy.
- Docs should describe behavior once and link elsewhere for detail.
