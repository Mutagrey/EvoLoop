# Codex Task: Prepare Project Architecture Before Expanding Pi-Inspired Agent System

## Context

This project is an evolving local-first AI agent system inspired by concepts from `earendil-works/pi`, but it is not a direct port and must not become a mechanical clone.

The project has already started to partially move toward a Pi-like architecture, but the current codebase is not yet clean enough to safely add more features.

Before adding new capabilities, tools, skills, memory, TUI features, session trees, approval gates, or complex agent flows, the project must be cleaned, structured, simplified, and prepared for long-term scaling.

The goal of this task is **not to add many new features**.

The goal is to make the existing project understandable, modular, maintainable, and ready for future Pi-inspired architecture work.

---

# Main Goal

Prepare the project architecture by:

- analyzing the current codebase
- identifying architectural problems
- removing dead or duplicated code
- reducing spaghetti logic
- separating responsibilities
- improving folder/module boundaries
- making the agent runtime easier to reason about
- preparing clean extension points for future Pi-like concepts
- documenting the intended architecture
- avoiding unnecessary feature expansion during cleanup

This task should prioritize **clarity, structure, and correctness** over speed.

---

# Important Rules

## Do Not

- Do not blindly rewrite the whole project from scratch.
- Do not mechanically copy architecture from Pi.
- Do not add large new features unless required for cleanup.
- Do not create unnecessary abstractions.
- Do not introduce new external dependencies unless absolutely necessary.
- Do not hide old code behind new wrappers without cleaning it.
- Do not leave duplicate implementations of the same concept.
- Do not keep unused experimental code “just in case”.
- Do not mix UI, agent runtime, tools, storage, prompts, and LLM calls in the same layer.
- Do not create vague folders like `Helpers`, `Utils`, `Managers`, `Services` without clear ownership.
- Do not perform cosmetic refactors only.
- Do not change behavior silently without documenting it.

## Do

- First understand the current repo.
- Prefer small, safe, incremental refactors.
- Preserve currently working behavior unless there is a clear bug.
- Remove dead code after confirming it is unused.
- Consolidate duplicated logic.
- Separate domain logic from infrastructure.
- Make dependencies explicit.
- Use clear naming.
- Keep modules testable.
- Add or update documentation as part of the refactor.
- Leave the repo in a state where future agent features can be added cleanly.

---

# Desired Long-Term Architecture Direction

The project should move toward a clean local-first agent architecture with these conceptual areas:

```text
User Interface
  CLI / TUI / Web UI

Application Layer
  Commands
  Use cases
  Session orchestration

Agent Runtime
  Agent loop
  Step execution
  Planning / acting / observing
  Tool call handling
  Approval flow
  Event stream

LLM Layer
  OpenAI-compatible client
  Model adapter
  Prompt rendering
  Message formatting
  Tool schema formatting
  Fallback parsing for models without native tool calls

Tool System
  Tool registry
  Tool definitions
  Tool permissions
  Workspace-safe file tools
  Shell tools
  Search/read/write tools
  Approval requirements

Workspace Layer
  Workspace root
  Path safety
  File access policy
  Patch/diff workflow
  Project scanning

Skills / Prompts
  Reusable task skills
  System prompt fragments
  Agent behavior instructions
  Project-specific guidance
  AGENTS.md support

Memory / Sessions
  Session tree
  Message history
  Run logs
  Summaries
  Local JSON/database storage

Infrastructure
  File system
  HTTP clients
  Local storage
  Logging
  Configuration
```

This does not mean all parts must be implemented now.

For this task, prepare the codebase so these areas can exist cleanly later.

---

# First Step: Repository Audit

Before editing code, inspect the repo and create a short architecture audit.

Create or update:

```text
docs/architecture/current-state.md
```

This document should include:

## 1. Current Project Overview

Describe what currently exists:

- entry points
- main runtime flow
- UI/CLI/TUI flow
- how LLM calls are made
- how tools are represented
- how tool calls are parsed
- how workspace access works
- how prompts are stored/rendered
- how sessions/history are stored
- where configuration is loaded
- where logs/events are produced

## 2. Current Problems

List concrete problems found in the repo, for example:

- duplicated logic
- unclear ownership
- mixed responsibilities
- dead files
- unused abstractions
- inconsistent naming
- files that are too large
- fragile parsing
- direct infrastructure access from high-level code
- UI directly controlling runtime logic
- tools coupled to LLM-specific formats
- prompt strings scattered in code
- unsafe workspace/file access
- missing tests around critical behavior

## 3. Risk Areas

Identify areas where refactoring could easily break behavior.

Examples:

- tool call parsing
- agent loop termination
- file write operations
- shell execution
- message history format
- model API compatibility
- streaming output
- TUI rendering

## 4. Suggested Refactor Order

Propose a safe sequence of changes.

Do not start large changes before writing this audit.

---

# Second Step: Target Architecture Document

Create or update:

```text
docs/architecture/target-architecture.md
```

This should describe the intended structure after cleanup.

Include:

## 1. Main Layers

Recommended logical layers:

```text
UI Layer
Application Layer
Agent Runtime
LLM Adapter Layer
Tool Layer
Workspace Layer
Storage Layer
Infrastructure Layer
```

## 2. Ownership Rules

Define which layer is allowed to depend on which layer.

Example:

```text
UI may call Application.
Application may call Agent Runtime.
Agent Runtime may call Tool Registry, LLM Adapter, Storage, Workspace.
Tools may call Workspace and Infrastructure.
LLM Adapter must not know about UI.
Storage must not know about UI.
UI must not directly execute tools.
UI must not directly call low-level file operations for agent actions.
```

## 3. Core Interfaces

Define the core interfaces/protocols/classes that should exist or be preserved.

Examples:

```text
IAgentRunner
IAgentSession
ILLMClient
IModelAdapter
ITool
IToolRegistry
IToolExecutor
IWorkspace
IApprovalPolicy
ISessionStore
IPromptRenderer
IEventSink
```

Do not create all of them blindly if the current codebase does not need them yet.

Use this as a target direction.

## 4. Data Flow

Document the desired agent execution flow:

```text
User input
 -> UI command
 -> Application use case
 -> Agent session
 -> Prompt/message construction
 -> LLM request
 -> LLM response
 -> Tool call detection
 -> Approval check
 -> Tool execution
 -> Observation added to session
 -> Next agent step
 -> Final response
```

## 5. Tool Flow

Document how tools should work:

```text
Tool definition
 -> registered in ToolRegistry
 -> exposed to model through ModelAdapter
 -> selected by model or parsed from fallback format
 -> validated
 -> approval policy checked
 -> executed with workspace-safe context
 -> result returned as observation
```

---

# Third Step: Cleanup Plan

Create:

```text
docs/architecture/refactor-plan.md
```

This should be a practical checklist grouped into phases.

Use this structure:

## Phase 1: Inventory and Dead Code Removal

- list unused files
- list duplicate implementations
- list old experimental concepts
- list obsolete TODOs
- remove code that is definitely unused
- keep a short note explaining removed areas

## Phase 2: Folder and Namespace Cleanup

- propose new folder structure
- move files without changing behavior where possible
- update imports/namespaces
- avoid mixing unrelated concerns

## Phase 3: Agent Runtime Separation

- separate agent loop from UI
- separate LLM request logic from runtime logic
- separate tool execution from tool parsing
- make the step lifecycle explicit

## Phase 4: Tool System Cleanup

- consolidate tool definitions
- create one registry path
- remove duplicate tool execution logic
- normalize tool result format
- make path/workspace validation centralized

## Phase 5: Prompt and Skill Cleanup

- move scattered prompts into clear prompt/skill files
- define how prompts are loaded/rendered
- prepare for future `skills/` support
- prepare for future `AGENTS.md` support

## Phase 6: Storage and Session Cleanup

- clarify where session history is stored
- define message/event/run-log format
- remove ad-hoc persistence
- prepare for session tree support later

## Phase 7: UI/TUI Boundary Cleanup

- ensure UI displays state but does not own agent logic
- define event stream from runtime to UI
- avoid direct business logic inside TUI components
- prepare for richer Claude-Code-like interface later

## Phase 8: Tests and Safety Checks

- add minimal tests around critical behavior
- test tool parsing
- test path safety
- test agent loop termination
- test model adapter formatting
- test config loading

---

# Recommended Folder Structure

Adapt this to the actual language and project conventions.

Do not force this exact structure if the existing repo has a better idiomatic structure, but move toward similar boundaries.

```text
/src
  /App
    AppHost
    DependencyInjection
    Configuration

  /Cli
    Commands
    CliProgram
    OutputFormatting

  /Tui
    Screens
    Components
    EventRendering
    InputHandling

  /Agent
    Runtime
    Sessions
    Steps
    Events
    Approvals

  /LLM
    Clients
    Adapters
    MessageFormatting
    ToolSchemaFormatting
    ResponseParsing

  /Tools
    Registry
    Definitions
    Execution
    Results
    BuiltIn

  /Workspace
    WorkspaceContext
    PathSafety
    FileSystemAccess
    DiffPatch

  /Prompts
    Rendering
    Templates
    Skills
    AgentsMd

  /Storage
    SessionStore
    JsonStore
    RunLogs

  /Infrastructure
    Http
    Logging
    SystemClock
    Environment

/tests
  /Agent.Tests
  /Tools.Tests
  /Workspace.Tests
  /LLM.Tests

/docs
  /architecture
    current-state.md
    target-architecture.md
    refactor-plan.md
    decisions.md

/skills
  README.md

/prompts
  system
  agents
  tools
```

---

# Pi-Inspired Concepts To Prepare For Later

The project should be prepared for these concepts, but they do not all need to be implemented now.

## 1. Agent Loop

Future target:

```text
while not done:
  build context
  ask model
  parse assistant output
  detect tool calls or final answer
  validate action
  request approval if needed
  execute tool
  append observation
  emit events to UI
```

Current task:

- make the existing loop understandable
- avoid hidden recursive flows
- avoid UI-owned execution logic
- make termination conditions explicit

## 2. Tool Registry

Future target:

```text
ToolRegistry
  - name
  - description
  - input schema
  - permission level
  - execution handler
```

Current task:

- remove duplicate registries
- ensure every tool has one canonical definition
- ensure execution is separate from schema formatting
- ensure model-specific formatting lives in LLM adapter layer

## 3. Model Adapter

Future target:

Different models may support different tool formats:

```text
Native OpenAI-style tool calls
JSON-in-text fallback
XML-like fallback
plain ReAct-style fallback
```

Current task:

- isolate model-specific response parsing
- do not let the whole runtime depend on one model format
- prepare for corporate APIs that may not support native tools
- make fallback parsing explicit and testable

## 4. Skills

Future target:

```text
skills/
  coding/
  debugging/
  refactoring/
  planning/
  repo-analysis/
```

Current task:

- prepare a clean place for skills
- do not hardcode all behavior in one giant system prompt
- document how skills will be loaded later

## 5. AGENTS.md Support

Future target:

The agent can read project-local guidance from:

```text
AGENTS.md
```

Current task:

- prepare architecture for loading project instructions
- avoid scattering project rules throughout code

## 6. Approval Gates

Future target:

Dangerous operations require approval:

```text
safe read: no approval
file write: approval depending on mode
shell command: approval depending on command
delete operation: approval required
network operation: approval required
```

Current task:

- identify where dangerous actions happen
- centralize approval decisions
- do not leave direct file/shell calls in random places

## 7. Event Stream

Future target:

Runtime emits events:

```text
UserMessageReceived
AssistantMessageStarted
ModelRequestStarted
ModelResponseReceived
ToolCallRequested
ApprovalRequired
ToolExecutionStarted
ToolExecutionFinished
FileChanged
AgentStepCompleted
FinalAnswerProduced
```

Current task:

- avoid printing directly from deep runtime code
- prepare clean event objects or structured callbacks
- let UI/TUI render events

---

# Cleanup Standards

When refactoring, follow these standards:

## File Size

- Prefer smaller files with clear responsibility.
- Large files are allowed only if they are cohesive.
- If a file mixes unrelated concerns, split it.

## Naming

Use names that describe responsibility.

Avoid:

```text
Manager
Helper
Utils
Processor
Handler
Service
```

Unless the role is genuinely clear.

Prefer:

```text
ToolRegistry
ToolExecutor
AgentRunner
PromptRenderer
WorkspacePathValidator
SessionStore
ModelResponseParser
```

## Dependencies

Make dependencies explicit through constructors or clear composition roots.

Avoid:

- hidden globals
- static mutable state
- direct environment access throughout the app
- random file system calls in business logic
- model clients created deep inside runtime code

## Error Handling

Errors should be structured.

Avoid:

- swallowing exceptions
- returning vague strings for all failures
- mixing user-facing error text with internal error state

Prefer:

```text
Result / Error object
ToolExecutionResult
ModelCallResult
WorkspaceError
ApprovalDenied
InvalidToolInput
```

## Logging

Logging should be useful but not noisy.

Do not log secrets.

Do not log full API keys, tokens, or private paths unnecessarily.

---

# Refactoring Workflow

Use this workflow:

## Step 1

Read the repository and produce:

```text
docs/architecture/current-state.md
docs/architecture/target-architecture.md
docs/architecture/refactor-plan.md
```

Do not make large code changes before these docs exist.

## Step 2

Implement cleanup in small batches.

Each batch should have:

```text
Goal:
Files changed:
Behavior changed:
Behavior preserved:
Risk:
How to verify:
```

## Step 3

After each batch, update:

```text
docs/architecture/refactor-plan.md
```

Mark completed items.

## Step 4

When removing code, document why it was removed.

## Step 5

When moving code, avoid changing behavior at the same time unless necessary.

## Step 6

When changing behavior, explain why.

---

# Verification

After cleanup, verify that:

- the project builds
- existing basic flows still work
- CLI/TUI entrypoint still runs
- LLM config still loads
- basic chat request works
- basic tool call works or fails with a clear structured error
- file read/write tools respect workspace safety
- agent loop terminates correctly
- no obvious duplicate tool systems remain
- no dead experimental folders remain
- prompts are not scattered randomly
- architecture docs match the actual code

Do not run expensive or irrelevant commands unnecessarily.

If full tests cannot be run, explain why and run the smallest useful verification.

---

# Expected Final Result

At the end of this task, the repo should be cleaner and ready for future work.

Expected deliverables:

```text
docs/architecture/current-state.md
docs/architecture/target-architecture.md
docs/architecture/refactor-plan.md
docs/architecture/decisions.md

Cleaned folder structure
Removed dead code
Reduced duplicated logic
Clearer agent runtime boundary
Clearer tool system boundary
Clearer LLM adapter boundary
Clearer UI/TUI boundary
Basic tests or verification notes
```

---

# Final Report Format

When finished, provide a final report in this format:

```text
# Architecture Preparation Report

## Summary

Briefly explain what was cleaned and why.

## Important Findings

List the most important architectural problems found.

## Changes Made

Grouped by area:

- Agent runtime
- Tool system
- LLM adapter
- Workspace
- Prompts/skills
- Storage/sessions
- UI/TUI
- Tests/docs

## Removed Code

List what was removed and why.

## Behavior Changes

List any behavior changes.

If none, say:

No intentional behavior changes.

## Risks Remaining

List remaining fragile areas.

## Recommended Next Steps

Give the next 3-7 tasks in priority order.

## Verification

List commands/tests/checks that were run and their results.
```

---

# Critical Instruction

This task is architecture preparation, not feature expansion.

The best outcome is not “more code”.

The best outcome is:

- less duplicated code
- fewer unclear paths
- cleaner boundaries
- better documentation
- safer future implementation
- easier debugging
- easier support for Pi-inspired concepts later

---

# Extra Strict Mode

Do not start by implementing new Pi features. First audit, clean, document, and create stable architectural boundaries. New features are out of scope unless they are required to remove existing architectural debt.
