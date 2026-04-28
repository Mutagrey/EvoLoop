# Operating Modes

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

## Tool Fallbacks

- missing `git` -> git tools return explicit unavailable status
- missing `rg` -> lexical search uses filesystem scanning
- missing `sqlite3` -> JSONL event store
- non-writable workspace -> no persistent sessions or memory
