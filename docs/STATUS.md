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
- Refreshed the CLI presentation layer with a compact activity-first layout, clearer live step output, and a post-run action summary.
- Added Windows self-contained publish scripts and a VS Code publish task.
- Added committed Windows release bundle layout under `release/windows/` for GitHub distribution.
- Rewrote documentation around Windows-first, no-admin, low-dependency operation.
- Added explicit execution modes: `run`, `plan`, and `review`.
- Extended tool contracts with risk/category/mutation metadata and execution-aware `ToolContext`.
- Switched policy evaluation to metadata-first decisions with approval-mode and execution-mode gating.
- Added dedicated shell command policy with fallback-only shell execution, blocked restore/install flows, and stronger network command checks.
- Replaced `fs_patch` dependence on `git apply` with an internal patch service.
- Added workspace mutation snapshots, `workspace_undo`, and `workspace_snapshot_diff`.
- Hardened path safety for protected paths such as `.git/config`, hooks, and `.env*`.
- Made JSONL the canonical typed event log even when `sqlite3` is available.
- Added project source-of-truth document loading for `AGENTS.md`, architecture, operating modes, and status docs.
- Added normalized internal assistant/user/tool-result messages with first-class text, thinking, tool-call, and tool-result blocks.
- Added model adapter contracts and wired `ReActAgentLoop` through adapter-normalized `AssistantMessage` results while preserving the public CLI loop.
- Added OpenAI-compatible native non-streaming and streaming tool-call parsing, including fragmented streaming `delta.tool_calls` argument accumulation.
- Preserved JSON-ReAct as the default fallback mode and formalized plain-text `Action`/`Arguments` recovery as a last-resort parser.
- Added JSON Schema conversion for tool schemas, tightened obvious schema defaults, and added runtime validation for `fs_patch` content/diff requirements.
- Added progressive-disclosure skills indexing for `.evoloop/skills/*/SKILL.md`.

## Current Problems

- No packaged Windows smoke test has been executed yet against the new `win-x64` publish path.
- The agent still depends on a remote/local model gateway for autonomous `run` and `plan` execution; `local-only degraded` mode is still diagnostic-safe, not a replacement for a local model runtime.
- `dotnet run --project tests/Agent.Tests/Agent.Tests.csproj` can still hang in this macOS workspace while spawning MSBuild child nodes; build the solution first and run the compiled test DLL directly as documented in `docs/TESTING.md`.
- Snapshot diff output is currently optimized for the most recent mutation, not for a full multi-file workspace review baseline.

## Next Improvements

- Resolve the executable-project build hang in this macOS workspace and re-run the full test harness.
- Smoke-test the self-contained Windows artifact on a restricted non-admin machine.
- Add richer review summarization for multi-file snapshot diffs and directory deletions.
- Add tests for non-writable snapshot storage, undo failure recovery, and review-mode fallback behavior.
- Exercise native tool calling against real corporate OpenAI-compatible gateways in all supported modes.
- Remove leftover machine-specific clutter files from version control as part of a dedicated cleanup pass.
