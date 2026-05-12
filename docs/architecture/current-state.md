# Current State

This audit describes the current implementation shape before any large Pi-inspired expansion. Canonical runtime behavior remains documented in [../ARCHITECTURE.md](../ARCHITECTURE.md), operating semantics in [../OPERATING-MODES.md](../OPERATING-MODES.md), and progress in [../STATUS.md](../STATUS.md).

## Current Project Overview

EvoLoop is a Windows-first local coding agent split into these projects:

- `Agent.Cli`: pure CLI entry point, single-turn commands, legacy REPL, console approval, and ANSI rendering.
- `Agent.Tui`: separate Terminal.Gui executable with a minimal shell; it records input but does not run agent tasks yet.
- `Agent.Hosting`: shared startup, workspace resolution, config/capability setup, and runtime wiring.
- `Agent.Core`: focused contract/config files, path safety, policy, ReAct loop, model adapter contracts, prompt/context builders, recovery logic, and tool-turn execution.
- `Agent.Tools`: file, patch, snapshot, git, search, and shell tools.
- `Agent.Providers`: custom and OpenAI-compatible HTTP model clients, native tool-call parsing, streaming accumulation, and JSON fallback adaptation.
- `Agent.Storage`: JSONL session/event stores, optional sqlite projection, workspace memory, and project identity.

Startup flow:

1. CLI/TUI applies .NET privacy defaults.
2. `AgentRuntimeContext.CreateAsync` resolves the workspace root, loads config, and probes capabilities.
3. `AgentExecutionHost.Create` wires model router, tools, patch service, search service, policy, event stores, memory store, context factory, and `ReActAgentLoop`.
4. CLI commands call `CliSession.RunTaskAsync`; TUI currently only renders a static interactive shell.

Runtime flow:

- `ReActAgentLoop.RunAsync` creates a session, builds initial messages, chooses profile/tool-calling mode, calls a model adapter, normalizes the assistant result, validates and repairs tool decisions, checks policy/approval, executes tools through `DefaultToolTurnExecutor`, appends observations, persists events, updates memory, and repeats until final, clarify, error, or max steps.
- Native OpenAI tool calls, streaming tool calls, JSON-ReAct fallback, and plain-text recovery all normalize into `AssistantMessage`/`ToolCallBlock` before execution.
- The model never writes files or runs commands directly; local effects pass through policy and tools.

Tools:

- `ToolCatalog.CreateDefaultTools()` is the only active default registry path.
- Each tool implements `ITool` and exposes `ToolSchema` plus `ToolMetadata`.
- File mutations go through `WorkspacePatchService`; reads/listing still access `File`/`Directory` directly after path resolution.
- Shell is represented by `exec_shell`, marked fallback-only, and constrained by policy.

Workspace and storage:

- `PathSafety` is the central workspace path guard in Core.
- Tool path resolution is a thin wrapper in `ToolPath`.
- Mutations snapshot into `.evoloop/storage/snapshots`; latest undo uses `last-mutation.json`.
- JSONL is canonical for sessions/events. Sqlite is optional and driven by the `sqlite3` CLI when available.
- Memory persists under workspace storage and may mirror to a per-user `.evoloop` path.

Prompts and project context:

- `DefaultPromptBuilder` builds the system prompt in code.
- `DefaultContextBuilder` injects `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/OPERATING-MODES.md`, `docs/STATUS.md`, runtime capabilities, task text, memory, and a progressive-disclosure skill index from `.evoloop/skills/*/SKILL.md`.
- There is no separate prompt template directory yet.

Events and UI:

- Runtime emits `AgentRunEvent` values to `IAgentRunObserver`.
- CLI renders these with `SpinnerObserver`.
- TUI has its own transcript and slash command model, but no runtime observer or approval bridge yet.

## Current Problems

- `ReActAgentLoop` is the main hotspot. It owns step orchestration, model fallback switching, response validation, deterministic recovery, path hints, history compaction, memory save, event writes, and final/error handling.
- `CliSession` mixes REPL command parsing, local degraded review fallback, runtime task dispatch, config formatting, command history, and undo.
- `Agent.Core` is not pure runtime logic. It directly reads files, directories, environment variables, and starts capability probe processes.
- Provider clients duplicate prompt fallback, response-format fallback, success-code checks, message shaping, and JSON-ReAct fallback wrapping.
- Tool/workspace concerns are split awkwardly: `PathSafety` is in Core, `ToolPath` is in Tools, and path scanning rules are duplicated in search and ReAct path hints.
- Snapshot manifest shape is duplicated in `WorkspacePatchService` and `WorkspaceSnapshotDiffTool`.
- Small string/path helpers are duplicated: `CommandExists`, auth detection, `ToOneLine`, `NormalizePath`, and `Clip`.
- `SpinnerObserver` infers activity by parsing user-facing tool result text, which couples UI summaries to exact tool messages.
- TUI docs now point to one canonical source per topic, but tracked release bundles still contain older CLI-only artifacts.
- `release/windows` contains large tracked binaries by design, but they are a high-churn area and currently out of sync with the TUI target.
- Tests use one large custom harness file, which makes ownership harder as subsystems grow.

No definitely removable production code was confirmed in this audit. The remaining confirmed stale area is release-artifact drift, not unused runtime classes.

## Phase 1 Inventory Result

- No tracked production `.cs` file was proven dead. SDK-style projects compile all tracked source files under each project, and tests reference both CLI and TUI projects.
- No runtime code was removed in Phase 1.
- `release/windows` remains committed by decision, but the checked-in bytes are stale CLI-only snapshots and must be regenerated before distribution.
- TUI documentation ownership is explicit: `docs/TUI_SPEC.md` is the current behavior spec, `docs/TUI_USAGE.md` is usage, `docs/TUI_AUDIT.md` and `docs/TUI_DEPENDENCY_AUDIT.md` are audit records, and `docs/EvoLoop_TUI_SPEC.md` is historical planning input.

## Duplicated Or Dead Areas

- Duplicate capability helpers:
  - `RuntimeCapabilityProbe.CommandExists`
  - `ProcessRunner.CommandExists`
  - `AgentStartup.HasApiAuthConfigured`
  - `RuntimeCapabilityProbe.HasApiAuthConfigured`
- Duplicate provider fallback flow:
  - `OpenAiCompatibleClient.SendWithPromptFallbackAsync`
  - `CustomGatewayClient.SendWithPromptFallbackAsync`
  - response-format fallback and `IsSuccessStatusCode` in both clients.
- Duplicate snapshot manifest records:
  - `WorkspacePatchService.MutationSnapshotManifest`
  - `WorkspaceSnapshotDiffTool.MutationSnapshotManifest`
- Duplicate path scanning rules:
  - `SafeWorkspaceFileEnumerator`
  - `ReActPathHints.ShouldSkipPathScan` and related binary extension logic.
- Stale documented state:
  - minimal TUI is intentionally pending integration.
  - checked-in Windows bundles have not been regenerated for `Agent.Tui`.

## Mixed Responsibilities

- CLI performs application behavior in `RunLocalReviewAsync` instead of delegating a shared application/runtime use case.
- Runtime directly shapes many model recovery prompts and fallback decisions instead of delegating a step lifecycle component.
- Providers both send HTTP requests and partially own model-format policy/fallback mechanics.
- Tools own both business operation and low-level filesystem/process calls.
- Storage handles canonical JSONL and sqlite projection in one file.
- UI observer parses tool result strings to derive activity summaries instead of consuming structured file/command/search events.

## Risk Areas

- Tool-call parsing and recovery: native, streaming, JSON-ReAct, plain text, aliases, deterministic repairs, and profile switching all feed the same execution path.
- Agent loop termination: max steps, invalid response thresholds, final-without-tool rejection, clarify, and queued multi-tool native calls are tightly coupled.
- Path safety and symlink handling: policy, tool path resolution, patch service, and direct reads/lists must stay aligned.
- Patch/undo: built-in diff apply, snapshot capture, directory delete/restore, hash preconditions, and latest-mutation semantics are fragile.
- Shell policy: fallback-only behavior, offline strict network checks, blocked fragments, and approval mode rules must remain compatible.
- JSONL/session/memory formats: existing local histories may rely on current files and field shapes.
- Provider compatibility: corporate OpenAI-compatible gateways may reject system prompts, response_format, native tools, or streaming shapes.
- TUI integration: connecting runtime events and approvals without importing CLI behavior or Terminal.Gui types into Core/Hosting.
- Windows packaging wrappers: `agent.cmd`, `agent-cli.cmd`, self-contained publish, vendored packages, and tracked release bundles are compatibility surfaces.

## Suggested Refactor Order

1. Create architecture docs and decisions before code changes.
2. Reconcile stale TUI/release documentation and decide how tracked Windows bundles are maintained.
3. Move CLI local review/runtime glue into a shared application/hosting use case.
4. Deduplicate provider fallback helpers in `ModelClientBase`.
5. Centralize snapshot manifest and workspace path scan rules.
6. Add structured activity/file/search/command event data so UI stops parsing result text.
7. Connect TUI to runtime only after the observer and approval boundary is stable.
