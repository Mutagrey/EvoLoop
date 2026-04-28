# Architecture

## Summary

EvoLoop is a model-backed coding agent CLI with explicit fallback behavior for restricted environments. The architecture is organized around two central rules:

- **environment capability detection happens before agent execution**
- **the model proposes actions, but the local harness decides whether they are allowed and performs them**

## Components

- `Agent.Cli`
  Startup, CLI parsing, REPL, `doctor`, command dispatch for `run`, `plan`, and `review`, degraded-mode gating.
- `Agent.Core`
  Contracts, execution modes, approval policy, ReAct loop, prompt/context builders, tool-turn orchestration, runtime capability model.
- `Agent.Tools`
  File, git, shell, search, snapshot diff, and undo tools. Tools expose metadata such as risk, category, mutation behavior, and required capabilities.
- `Agent.Providers`
  Model gateway access only. No policy or workspace logic belongs here.
- `Agent.Storage`
  Session and memory persistence. JSONL is the canonical event log; optional SQLite is only a projection/cache when available.

## Startup Flow

1. Resolve workspace root.
2. Load effective config.
3. Probe runtime capabilities.
4. Choose operating mode.
5. Build tool/search/provider stack with capability-aware fallbacks, patch service, and JSONL event log.
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

## Agent Harness

The core execution model is a local agent harness:

1. build prompt and context from policy, project instructions, runtime capabilities, task, and memory
2. ask the model for one structured decision
3. parse the decision into `tool`, `final`, or `clarify`
4. validate the tool call against tool schema, execution mode, approval mode, and command policy
5. optionally request user approval
6. execute the tool locally
7. append observations and typed events
8. repeat until final answer or stop condition

The model never writes files or runs commands directly. File and shell effects always pass through the harness.

## Execution Modes

- `run`
  Normal agent execution with tool use subject to policy.
- `plan`
  Analysis-only execution. Mutating tools, `exec_shell`, staging, and commits are denied by policy.
- `review`
  Inspection-only execution. The agent should prefer `git_diff` when `git` exists and `workspace_snapshot_diff` otherwise.
- `interactive`
  REPL surface that can dispatch `run`, `plan`, and `review` turns.

## Operating Contract

- If gateway/model access is available, the agent can execute model-backed tasks.
- If gateway/model access is unavailable, the CLI still starts in `local-only degraded` mode.
- If workspace storage is unavailable, the CLI uses non-persistent fallbacks instead of crashing.
- If `rg` is unavailable, lexical search uses the built-in scanner.
- If `sqlite3` is unavailable, JSONL remains the canonical event log and no SQLite projection is created.

## Policy Model

- Every tool declares metadata: risk level, category, whether it mutates the workspace, and required capabilities.
- Policy decisions are metadata-first, not name-first. Name matching remains only for shell command inspection.
- Approval modes:
  - `ReadOnly`
  - `WorkspaceWrite`
  - `AutoEdit`
  - `DangerFullAccess`
- `exec_shell` is fallback-only and must not be the default mechanism when specialized tools can do the work.
- `plan` and `review` modes deny workspace mutations even if the model requests them.

## Workspace Mutation Model

- File writes, patches, and deletes go through the patch service instead of raw `git apply`.
- Every mutation captures a snapshot under `.evoloop/storage/snapshots`.
- The latest mutation can be reverted via `workspace_undo` or REPL `/undo`.
- Protected paths such as `.git/config`, hooks, SSH material, and `.env*` are denied by path safety rules.

## Observability

- Session steps still persist for run history.
- Typed JSONL events also persist under `.evoloop/storage/events.jsonl`.
- Event log records include session start/end, model requests, tool calls, tool results, approvals, file mutations, and final answer.

## Boundaries

- CLI decides whether the run may start.
- Core decides how the agent loop behaves and how prompt/context/policy/tool execution are composed.
- Tools do not guess capability state; they read it from `ToolContext`.
- Providers do not know about workspace policy.
- Docs should describe behavior once and link elsewhere for detail.
