# TUI Audit

## Current Shape

- CLI entry point: `src/Agent.Cli/Program.cs`.
- TUI entry point: `src/Agent.Tui/Program.cs`.
- Shared host: `src/Agent.Hosting` builds `ReActAgentLoop` from `Agent.Core`, `Agent.Tools`, `Agent.Providers`, and `Agent.Storage`.
- Tools: created through `ToolCatalog.CreateDefaultTools()`.
- Tool parsing/execution: provider responses are normalized by `Agent.Core`/`Agent.Providers`; policy and execution stay in `Agent.Core`.
- Sessions: `HybridEventStore`, JSONL event log, and workspace memory under `.evoloop/storage` when writable.
- Streaming/process visibility: `IAgentRunObserver` emits typed `AgentRunEvent` values; CLI currently renders them through `SpinnerObserver`.
- Approval flow: `ConsoleApprovalService` implements `IApprovalService` for the current console UI.
- Existing CLI rendering: custom `AnsiRenderer`.
- Existing TUI rendering: minimal `Agent.Tui` shell uses `Terminal.Gui`; testable command/transcript logic stays independent of Terminal.Gui.

## Minimal Integration Path

- Keep separate executable targets: `Agent.Tui` and `Agent.Cli`.
- Use packaged `agent.cmd` for `Agent.Tui.exe`.
- Use packaged `agent-cli.cmd` for `Agent.Cli.exe`.
- Preserve explicit pure CLI commands: `doctor`, `run`, `plan`, `review`, and `repl` in `Agent.Cli`.
- Keep shared runtime wiring in `Agent.Hosting`.
- Do not expose Terminal.Gui types to `Agent.Core`, `Agent.Tools`, `Agent.Providers`, or `Agent.Storage`.
- Connect the next TUI phase through existing `IAgentRunObserver` and `IApprovalService` boundaries instead of moving policy/runtime behavior into the UI.

## Layer Ownership

- TUI: terminal application shell and interactive rendering.
- CLI: command parsing, REPL dispatch, diagnostics, single-turn commands, console approval UI.
- Hosting: shared startup, capability probing, config loading, workspace resolution, and agent wiring.
- Core: agent loop, normalized messages/events, policy, path safety, execution modes.
- Tools: local file/git/search/shell/snapshot/patch operations.
- Providers: model gateway request/response adaptation only.
- Storage: event, session, and memory persistence/fallbacks.
