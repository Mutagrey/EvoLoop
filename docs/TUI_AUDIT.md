# TUI Audit

## Current Shape

- CLI entry point: `src/Agent.Cli/Program.cs`.
- Runtime invocation: `Agent.Cli` builds `ReActAgentLoop` from `Agent.Core`, `Agent.Tools`, `Agent.Providers`, and `Agent.Storage`.
- Tools: created through `ToolCatalog.CreateDefaultTools()`.
- Tool parsing/execution: provider responses are normalized by `Agent.Core`/`Agent.Providers`; policy and execution stay in `Agent.Core`.
- Sessions: `HybridEventStore`, JSONL event log, and workspace memory under `.evoloop/storage` when writable.
- Streaming/process visibility: `IAgentRunObserver` emits typed `AgentRunEvent` values; CLI currently renders them through `SpinnerObserver`.
- Approval flow: `ConsoleApprovalService` implements `IApprovalService` for the current console UI.
- Existing console rendering: custom `AnsiRenderer`; no external console/TUI library is referenced by projects.

## Minimal Integration Path

- Keep one executable: `Agent.Cli`.
- Make bare `agent` and `agent tui` enter the TUI path.
- Preserve explicit pure CLI commands: `agent run`, `agent plan`, `agent review`, `agent doctor`.
- Preserve old line-based REPL as `agent repl`.
- Keep TUI code in focused CLI/TUI classes until a separate library is justified.
- Do not expose Terminal.Gui types to `Agent.Core`, `Agent.Tools`, `Agent.Providers`, or `Agent.Storage`.

## Layer Ownership

- CLI: command parsing, startup, capability warnings, TUI/REPL dispatch, console approval UI.
- Core: agent loop, normalized messages/events, policy, path safety, execution modes.
- Tools: local file/git/search/shell/snapshot/patch operations.
- Providers: model gateway request/response adaptation only.
- Storage: event, session, and memory persistence/fallbacks.
