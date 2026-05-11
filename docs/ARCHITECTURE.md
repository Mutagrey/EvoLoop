# Architecture

## Summary

EvoLoop is a model-backed coding agent CLI with explicit fallback behavior for restricted environments. The architecture is organized around three central rules:

- **environment capability detection happens before agent execution**
- **the model proposes actions, but the local harness decides whether they are allowed and performs them**
- **provider-specific tool formats are normalized before the runtime executes anything**

## Components

- `Agent.Cli`
  Startup, CLI parsing, REPL, `doctor`, command dispatch for `run`, `plan`, and `review`, degraded-mode gating.
- `Agent.Core`
  Contracts, execution modes, approval policy, normalized message/tool-call model, model adapter contracts, ReAct-compatible runtime loop, prompt/context builders, tool-turn orchestration, runtime capability model.
- `Agent.Tools`
  File, git, shell, search, snapshot diff, and undo tools. Tools expose metadata such as risk, category, mutation behavior, and required capabilities.
- `Agent.Providers`
  Model gateway access and provider-specific request/response adaptation only. No policy or workspace logic belongs here.
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
2. choose a model adapter and tool-calling mode from profile configuration/capability probing
3. ask the model for a turn
4. normalize the provider response into internal assistant content blocks
5. extract first-class `ToolCallBlock` values or a final/clarifying text response
6. validate tool arguments against tool schema and runtime-specific checks
7. evaluate execution mode, approval mode, path safety, and command policy
8. optionally request user approval
9. execute the tool locally through `IToolTurnExecutor`
10. append a structured `ToolResultMessage` and typed events
11. repeat until final answer, approval wait/rejection, max steps, or error

The model never writes files or runs commands directly. File and shell effects always pass through the harness.

## Internal Message Model

The runtime uses normalized internal structures before tool execution:

- `UserMessage`
- `AssistantMessage`
- `TextBlock`
- `ThinkingBlock`
- `ToolCallBlock`
- `ToolResultMessage`
- `ToolCallId`
- `ToolName`
- `ToolResultContent`

Existing `ToolCall`, `ToolResult`, and `ITool` contracts remain compatible. `ToolCallBlock` converts into the existing `ToolCall` shape before policy and execution.

## Tool Calling Modes

Profiles default to `JsonReActFallback` for compatibility with restricted corporate gateways. Other modes are opt-in through model profile configuration.

- `NativeNonStreamingTools`
  Sends OpenAI-compatible `tools` and `tool_choice="auto"`, parses `choices[].message.tool_calls`, and returns tool results as `role="tool"` with `tool_call_id`.
- `NativeStreamingTools`
  Sends OpenAI-compatible native tools with `stream=true`, accumulates fragmented `choices[].delta.tool_calls[].function.arguments`, and normalizes the completed call.
- `JsonReActFallback`
  Sends no native tool list. The prompt requires one strict JSON object: tool, final, or clarify.
- `PlainTextRecoveryFallback`
  Last-resort parser for weak model output such as `Action: fs_read` plus `Arguments: {...}`.
- `Auto`
  Optional OpenAI-compatible mode that can probe native non-streaming tool support with a safe `evoloop_probe_noop` tool, then falls back to JSON-ReAct if unsupported or ignored.

Native tool support is never assumed. Provider-specific payloads and parsing stay in `Agent.Providers`; the runtime only executes normalized tool blocks.

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
- Tool validation failures, policy denials, approval rejections, thrown tool exceptions, and failed tools are represented as structured tool-error results for the next model turn.

## Workspace Mutation Model

- File writes, patches, and deletes go through the patch service instead of raw `git apply`.
- Every mutation captures a snapshot under `.evoloop/storage/snapshots`.
- The latest mutation can be reverted via `workspace_undo` or REPL `/undo`.
- Protected paths such as `.git/config`, hooks, SSH material, and `.env*` are denied by path safety rules.

## Observability

- Session steps still persist for run history.
- Typed JSONL events also persist under `.evoloop/storage/events.jsonl`.
- Event log records include session start/end, model requests, tool calls, tool results, approvals, file mutations, and final answer.

## Skills

Skills use progressive disclosure only. At startup the context builder scans `.evoloop/skills/*/SKILL.md`, extracts a name, short description, and relative path, and injects only that index. The model must read the full `SKILL.md` through `fs_read` before applying it.

## Boundaries

- CLI decides whether the run may start.
- Core decides how the agent loop behaves and how prompt/context/policy/tool execution are composed.
- Tools do not guess capability state; they read it from `ToolContext`.
- Providers do not know about workspace policy.
- EvoLoop does not import or depend on EvoLoopAI, and Pi is used only as architectural inspiration for normalized messages, adapters, first-class tool calls/results, and progressive disclosure.
- Docs should describe behavior once and link elsewhere for detail.
