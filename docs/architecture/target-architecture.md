# Target Architecture

This is the cleanup target, not a request to rewrite the project. Existing public CLI commands, tool names, config shape, project references, and storage formats should remain compatible unless a migration note is explicitly added.

## Main Layers

```text
UI Layer
  Agent.Cli
  Agent.Tui

Application / Hosting Layer
  startup
  config loading
  capability probing
  workspace resolution
  command/use-case dispatch
  dependency wiring

Agent Runtime
  session run lifecycle
  step execution
  model-turn orchestration
  tool-call validation
  approval flow
  event stream
  termination rules

LLM Adapter Layer
  HTTP model clients
  provider payload formatting
  native tool-call formatting/parsing
  JSON/plain-text fallback parsing
  model profile selection support

Tool Layer
  tool registry
  tool definitions
  tool execution
  tool result normalization
  file/git/search/shell built-ins

Workspace Layer
  workspace root
  path safety
  filesystem access
  patch/snapshot/undo
  safe project scanning

Storage Layer
  JSONL event/session logs
  memory store
  optional sqlite projection
  project identity

Infrastructure Layer
  process runner
  HTTP transport
  environment access
  clocks
  low-level file adapters where useful
```

The current project layout can keep separate assemblies. The cleanup goal is clearer ownership inside the existing assemblies first; physical moves should happen only when they reduce coupling without behavior changes.

## Ownership Rules

- UI may call Application/Hosting and render runtime events.
- UI must not execute tools directly for agent actions.
- UI must not own policy, path safety, model parsing, or session persistence.
- Application/Hosting owns startup, capability/config setup, and use-case dispatch.
- Agent Runtime owns the agent loop, step lifecycle, approvals, event emission, tool-turn coordination, and termination.
- Agent Runtime may depend on LLM adapters, tool registry/executor, workspace safety abstractions, and storage contracts.
- LLM adapters own provider protocol details only. They must not inspect workspace paths, approvals, command policy, or UI state.
- Tools execute local operations through a workspace-safe context and return structured results.
- Workspace owns path validation, snapshots, patch/undo, and safe scanning rules.
- Storage owns persistence and degraded no-op fallbacks.
- Infrastructure owns low-level process, environment, HTTP, and filesystem primitives when those primitives are shared.

## Core Interfaces To Preserve Or Grow Toward

Already useful and should remain recognizable:

- `IAgentLoop`
- `IAgentRunObserver`
- `IModelClient`
- `IModelClientRouter`
- `IModelAdapter`
- `IModelAdapterRouter`
- `ITool`
- `IPolicyEngine`
- `IApprovalService`
- `IToolTurnExecutor`
- `IToolContextFactory`
- `IContextBuilder`
- `IPromptBuilder`
- `IEventStore`
- `IEventLog`
- `IWorkspaceMemoryStore`
- `ISearchService`
- `IPatchService`
- `ICommandPolicy`

Likely future names after cleanup:

- `IAgentSessionRunner` for one session lifecycle if `IAgentLoop` is split.
- `IAgentStepExecutor` for one model/tool/observation step.
- `IToolRegistry` if default tool creation becomes configurable.
- `IWorkspace` or `IWorkspaceFileSystem` if direct `File`/`Directory` use needs isolation.
- `IPromptRenderer` if prompts move from code into templates.
- `IRunReviewService` for model-free review fallback currently inside CLI.

Do not create all of these at once. Add them only when extracting existing behavior.

## Desired Data Flow

```text
User input
 -> UI command or TUI submit
 -> Application use case
 -> Agent session request
 -> Runtime builds context and prompt
 -> LLM adapter formats request
 -> Provider returns response
 -> Adapter normalizes assistant message
 -> Runtime validates tool/final/clarify decision
 -> Policy and approval check
 -> Tool executor runs selected tool
 -> Workspace-safe operation occurs
 -> Storage records events/session steps
 -> Runtime appends observation
 -> UI renders event stream
 -> Runtime returns final result
```

Local degraded review should follow a separate application use case:

```text
Review command
 -> Application review service
 -> git evidence or snapshot evidence
 -> structured review summary
 -> UI renderer
```

## Desired Tool Flow

```text
Tool implementation
 -> registered once in tool catalog/registry
 -> schema converted by LLM adapter when needed
 -> model selects native tool call or fallback JSON tool call
 -> adapter normalizes to ToolCallBlock
 -> runtime validates arguments
 -> policy checks metadata, mode, path, shell, approval
 -> tool executor handles timeout/events/session step
 -> tool executes through workspace-safe context
 -> structured ToolResultMessage returns to model
```

Rules:

- Tool schema definition is model-agnostic.
- Model-specific schema formatting stays in the LLM adapter layer.
- Tool result text is user-readable, but event data should carry structured file/command/search details.
- `exec_shell` remains fallback-only and approval-controlled.
- Path validation is centralized and consistent for read, write, patch, delete, git path filters, and shell cwd.

## Prompt And Skill Direction

- Keep project source-of-truth loading from `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/OPERATING-MODES.md`, and `docs/STATUS.md`.
- Move large hardcoded prompt fragments behind a prompt renderer only when changing prompt behavior.
- Keep progressive disclosure for `.evoloop/skills/*/SKILL.md`: index first, read full skill on demand.
- Avoid scattering project rules into runtime recovery code.

## Storage And Sessions Direction

- JSONL remains canonical.
- Sqlite remains optional projection/cache only.
- Session start/end, model request, tool request/result, approval, mutation, memory, and final events should have stable event types.
- Future session tree support should layer on top of the canonical event stream instead of replacing it.

## UI/TUI Direction

- CLI and TUI stay separate executable targets.
- Shared behavior belongs in Hosting/Application, not in `Agent.Cli`.
- TUI should consume runtime events through `IAgentRunObserver` or a successor event stream.
- TUI approval should implement `IApprovalService` without reusing console prompt code.
- Terminal.Gui types must stay inside `Agent.Tui`.
