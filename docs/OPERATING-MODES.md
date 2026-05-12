# Operating Modes

## Runtime Modes

## `full`

Conditions:
- gateway configured
- gateway reachable

Behavior:
- model-backed agent tasks enabled
- normal tool execution flow
- standard policy and approval rules still apply

## `offline-strict`

Conditions:
- same as `full`
- `safety.offlineStrictMode = true`

Behavior:
- model-backed agent tasks enabled
- network shell commands denied unless they target approved hosts and require approval
- intended for controlled corporate/offline-adjacent environments

## `local-only degraded`

Conditions:
- gateway missing, invalid, or unreachable

Behavior:
- CLI still starts
- `doctor`, `config`, `tools`, history-like commands remain usable
- model-backed `/task` and `run` operations are blocked with a clear explanation
- storage may still work if workspace is writable

## Execution Modes

Execution mode is separate from runtime mode. Runtime mode describes environment capability; execution mode describes what the agent is allowed to do in a turn.

## `run`

Behavior:
- normal model-backed task execution
- mutating tools allowed subject to approval policy
- shell remains fallback-only and policy-controlled

## `plan`

Behavior:
- model-backed analysis is allowed when the gateway is available
- `fs_write`, `fs_patch`, `fs_delete`, `git_add`, `git_commit`, `workspace_undo`, and `exec_shell` are denied by policy
- intended for analysis and implementation planning without workspace side effects

## `review`

Behavior:
- review current changes without mutating the workspace
- prefer `git_diff` when `git` is available
- otherwise use `workspace_snapshot_diff` against `.evoloop/storage/snapshots`, including multi-file snapshot history when available
- if model access is unavailable, CLI can still emit a local review summary from git or snapshot evidence

## Approval Modes

- `ReadOnly`: read/search/review only
- `WorkspaceWrite`: file mutation allowed with approval; shell requires approval
- `AutoEdit`: file mutation allowed inside workspace; risky shell and commits still require approval
- `DangerFullAccess`: least restrictive mode, still bounded by workspace/path policy unless explicitly changed in code

## Tool Fallbacks

- missing `git` -> git tools return explicit unavailable status
- missing `rg` -> lexical search uses filesystem scanning
- missing `sqlite3` -> JSONL remains the only event store
- non-writable workspace -> no persistent sessions or memory
- unavailable model rerank -> `search_semantic` falls back to lexical-only output with explicit degraded messaging

## Model Tool Calling

Tool-calling mode is separate from runtime and execution mode.

- Existing model profiles default to `JsonReActFallback`.
- `JsonReActFallback` asks for one JSON object and one tool call per model turn; native tool modes can return multiple provider tool calls.
- `NativeNonStreamingTools`, `NativeStreamingTools`, and `Auto` are opt-in per model profile.
- `Auto` may probe a gateway with a safe no-op tool and falls back to JSON-ReAct when native tools are rejected or ignored.
- `local-only degraded` still blocks model-backed `run` and `plan` tasks before any model adapter is called.
- Profile switching is explicit through `runtime.profileFallbackOrder`; profile names do not imply fallback behavior by themselves.
