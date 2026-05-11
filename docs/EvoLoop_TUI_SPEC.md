# EvoLoop TUI Specification for Codex App

## Purpose

This document is a ready-to-use specification for Codex App.

Goal: design and implement a professional terminal UI for a local-first C#/.NET coding agent, similar in usability to Claude Code, but adapted to this project and to a restricted corporate Windows environment.

The TUI should provide:
- convenient chat interaction;
- slash commands with suggestions after typing `/`;
- visible agent process;
- useful rendering of messages, tool calls, diffs, approvals, and errors;
- local-first sessions and logs;
- offline-first vendored NuGet dependency strategy.

---

# 1. Project Context

We are building a local-first C#/.NET coding agent inspired by Claude Code-style terminal UX and by the architectural ideas of local coding agents.

Target environment:
- restricted corporate Windows machine;
- no admin rights;
- Visual Studio Community 2022;
- local .NET SDK;
- limited or no internet access;
- corporate OpenAI-compatible API accessed through a local proxy;
- localhost web/proxy may exist, but the TUI itself must work as a CLI/TUI executable;
- local-first storage using JSON/files;
- dependencies must be vendored and restorable offline.

Do not introduce:
- Node.js;
- npm;
- React;
- Vite;
- Python;
- Docker;
- cloud-only dependency;
- required online package restore;
- hidden dependency downloads during build.

The TUI should be the main interactive interface for the coding agent.

---

# 2. Main Goal

Implement a production-grade TUI layer for the local coding agent.

The TUI should support:

1. Chat input with multiline editing.
2. Slash command menu when the user types `/`.
3. Streaming assistant output.
4. Visible agent steps:
   - status updates;
   - tool calls;
   - file reads;
   - shell commands;
   - patches;
   - approvals;
   - errors.
5. Safe workspace operations.
6. Useful rendering of:
   - normal messages;
   - code blocks;
   - diffs;
   - tool results;
   - errors;
   - approval prompts.
7. Session history and session resume.
8. Keyboard-first UX.
9. Clean separation between TUI and agent runtime.
10. Testable components.

---

# 3. Important Constraint

Do not rewrite the whole project.

First inspect the existing repository structure and identify:
- current CLI entry point;
- current agent runtime;
- current tool registry;
- current session storage;
- current message model;
- current streaming support;
- current approval flow;
- current logging system;
- current dependency setup.

Then create a focused implementation plan.

If something is missing, introduce minimal clean abstractions instead of large rewrites.

---

# 4. Desired User Experience

The TUI should feel like a professional coding assistant running inside the terminal.

Example layout:

```text
┌──────────────────────────────────────────────────────────────┐
│ EvoLoop Agent                         model: qwen / glm / api │
├──────────────────────────────────────────────────────────────┤
│ Conversation                                                 │
│                                                              │
│ User: fix broken tower defense game                          │
│                                                              │
│ Assistant: I will inspect the project structure first.        │
│                                                              │
│ ▸ tool: list_files ./                                        │
│   result: 42 files                                           │
│                                                              │
│ ▸ tool: read_file Game.cs                                    │
│   result: found broken enemy pathing                         │
│                                                              │
│ ▸ patch: Game.cs                                             │
│   + fixed path update loop                                   │
│   + added null guard                                         │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ Status: waiting for input | tokens | cwd | session name       │
├──────────────────────────────────────────────────────────────┤
│ > /help                                                      │
└──────────────────────────────────────────────────────────────┘
```

The design should be useful even in a small terminal window.

Avoid overly complex UI at first. Prefer stability and clarity.

---

# 5. Dependency Policy

External .NET dependencies are allowed, but only if they are vendored and can be restored offline.

The project should use an offline-first NuGet strategy.

Preferred structure:

```text
vendor/
  nuget/
    Terminal.Gui.x.y.z.nupkg
    SomeDependency.x.y.z.nupkg

NuGet.Config
Directory.Packages.props
packages.lock.json
docs/
  DEPENDENCIES.md
  TUI_DEPENDENCY_AUDIT.md
```

Use local NuGet packages instead of copying raw DLLs manually.

Raw DLL references are allowed only as a last resort and must be justified in `docs/DEPENDENCIES.md`.

---

# 6. Preferred TUI Dependency

Prefer `Terminal.Gui` as the main TUI framework.

Reason:
- designed for full terminal UI applications;
- supports windows, dialogs, input controls, keyboard navigation, lists, menus, layout, and scrollable views;
- more suitable for Claude Code-like interactive UX than simple console printing.

Optional:
- `Spectre.Console` may be used only for non-fullscreen rendering, diagnostic commands, pretty tables, or fallback console output.
- Do not mix `Terminal.Gui` and `Spectre.Console` in the same rendering loop unless there is a clear reason.

Default recommendation:
- Use `Terminal.Gui` for the main interactive TUI.
- Do not add `Spectre.Console` unless the audit proves it is useful.

---

# 7. Vendor NuGet Layout

Create this structure if missing:

```text
vendor/
  nuget/
    README.md

docs/
  DEPENDENCIES.md
```

`vendor/nuget/README.md` should explain:
- which packages are stored there;
- exact versions;
- source URL or origin;
- license;
- why the dependency is needed;
- whether transitive dependencies are also vendored.

`docs/DEPENDENCIES.md` should contain a table:

```text
Package        Version   Purpose              License   Source
Terminal.Gui   x.y.z     Main TUI framework    MIT       nuget.org/github
```

---

# 8. NuGet.Config Requirement

Add a repository-level `NuGet.Config`.

It should prefer the local vendored feed.

Example:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="LocalVendorNuget" value="./vendor/nuget" />
  </packageSources>
</configuration>
```

Do not require nuget.org for normal restore.

If online restore is needed during development, create a separate optional config:

```text
NuGet.online.config
```

The default repository configuration must remain offline-first.

---

# 9. Locked Restore

Enable deterministic package restore.

Use `packages.lock.json`.

In project files or shared props, enable lock file usage:

```xml
<PropertyGroup>
  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
</PropertyGroup>
```

For CI/offline verification, use locked mode:

```bash
dotnet restore --locked-mode
```

If package versions change, update the lock file intentionally and document why.

Do not silently float package versions.

---

# 10. Central Package Versions

Prefer central package management.

Create or update:

```text
Directory.Packages.props
```

Example:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Terminal.Gui" Version="x.y.z" />
  </ItemGroup>
</Project>
```

Then project files should use:

```xml
<ItemGroup>
  <PackageReference Include="Terminal.Gui" />
</ItemGroup>
```

Avoid scattering package versions across many `.csproj` files.

---

# 11. Dependency Audit Phase

Before implementing the TUI, create:

```text
docs/TUI_DEPENDENCY_AUDIT.md
```

It must answer:

1. Is any TUI/console library already referenced?
2. Is `Terminal.Gui` already present?
3. Are any local `.nupkg` packages already vendored?
4. Is there a `NuGet.Config`?
5. Is package restore currently online-only?
6. Does the project have `packages.lock.json`?
7. Which dependencies are required for the first TUI version?
8. Which transitive dependencies must be vendored?
9. Can the project build with `dotnet restore --locked-mode` using only `vendor/nuget`?
10. If not, what is missing?

Do not start implementation until this audit exists.

---

# 12. Dependency Implementation Rules

When adding Terminal.Gui:

1. Do not run `dotnet add package` against nuget.org as a required step.
2. Add the `.nupkg` package to `vendor/nuget`.
3. Add any required transitive `.nupkg` packages to `vendor/nuget`.
4. Update `NuGet.Config`.
5. Update `Directory.Packages.props`.
6. Update project `.csproj`.
7. Generate or update `packages.lock.json`.
8. Verify restore using only local feed.
9. Document the dependency in `docs/DEPENDENCIES.md`.

The final implementation summary must include:
- packages added;
- versions;
- licenses;
- why each dependency is needed;
- whether restore works offline.

---

# 13. Fallback Strategy

If `Terminal.Gui` cannot be restored or does not work reliably in the current corporate environment, implement a simpler console backend behind the same abstraction:

```csharp
public interface ITuiBackend
{
    Task RunAsync(TuiApp app, CancellationToken ct);
}
```

Backends:

```text
EvoLoop.Tui.TerminalGuiBackend
EvoLoop.Tui.BasicConsoleBackend
```

Do not couple the agent runtime directly to Terminal.Gui types.

The TUI architecture must allow replacing the UI backend later.

---

# 14. Core Screens

Implement these screens or modes.

## 14.1 Chat Screen

Default screen.

Must show:
- user messages;
- assistant messages;
- system/status messages;
- tool calls;
- tool results;
- patches/diffs;
- approval requests;
- errors;
- final answer.

Message rendering should visually distinguish:
- user;
- assistant;
- tool;
- error;
- approval;
- patch;
- system status.

## 14.2 Slash Command Menu

When user types `/`, show suggestions.

Example:

```text
/help          Show available commands
/model         Change model
/session       Show current session
/sessions      List sessions
/new           Start new session
/resume        Resume previous session
/compact       Summarize current session context
/tools         Show available tools
/approvals     Show approval mode
/diff          Show latest diff
/status        Show agent status
/clear         Clear screen
/exit          Exit app
```

The menu should:
- filter as user types;
- support arrow up/down;
- support Enter to select;
- support Esc to close;
- insert command into input or execute directly depending on command type.

## 14.3 Tool Activity Panel

Tool calls should not be dumped as raw JSON.

Render them in a compact and useful way:

```text
▸ read_file src/Game.cs
  ok, 183 lines

▸ search "EnemyPath"
  found 4 matches in 2 files

▸ apply_patch src/Game.cs
  changed 24 lines
```

For failed tools:

```text
✗ build
  failed: CS0103: The name 'enemySpeed' does not exist
```

## 14.4 Approval Screen

If the agent wants to perform a sensitive action, show a clear prompt:

```text
Approval required

Action:
  apply patch to src/Game.cs

Summary:
  - modifies enemy movement loop
  - adds null checks
  - updates tower targeting

Allow?
  [y] yes   [n] no   [v] view diff   [a] always allow similar
```

Do not hide destructive actions.

Approval should be required for:
- writing files;
- deleting files;
- running commands;
- large patches;
- changing files outside workspace;
- network access, if supported later.

## 14.5 Diff Viewer

Must support rendering simple unified diffs.

Example:

```diff
- enemy.Position += direction;
+ enemy.Position += direction * deltaTime;
```

At minimum:
- show file path;
- show added/removed lines;
- support scrolling;
- support returning to chat.

Do not implement a complex full-screen editor initially.

## 14.6 Session List

Allow listing and resuming previous sessions.

```text
Sessions

> 2026-05-12  tower-defense-fix
  2026-05-11  tui-design
  2026-05-10  glm-tool-calls-debug
```

Each session should have:
- id;
- title;
- created date;
- updated date;
- model;
- workspace path;
- message count.

---

# 15. Keyboard Shortcuts

Implement a practical keyboard model.

Required:

```text
Enter             Send message or select menu item
Shift+Enter       New line in input if supported by backend
Ctrl+C            Cancel current generation / tool run
Ctrl+D            Exit if input is empty
Esc               Close popup / cancel selection
Up/Down           Navigate slash menu or input history
PageUp/PageDown   Scroll conversation
Ctrl+L            Clear visual screen
Ctrl+R            Search command/history later, optional
Tab               Accept suggestion/autocomplete
```

If the TUI library cannot detect Shift+Enter reliably, use `Alt+Enter` or document the limitation.

---

# 16. Slash Commands

Implement command system as a separate module, not hardcoded inside UI event handlers.

Suggested interface:

```csharp
public interface ISlashCommand
{
    string Name { get; }
    string Description { get; }
    string Usage { get; }
    Task ExecuteAsync(CommandContext context, string[] args, CancellationToken ct);
}
```

Commands to implement first:

## `/help`

Shows all commands.

## `/status`

Shows:
- current model;
- current workspace;
- current session;
- approval mode;
- available tools;
- API endpoint/proxy status if available.

## `/tools`

Lists registered tools and short descriptions.

## `/model`

Shows or changes model.

Examples:

```text
/model
/model qwen3-coder
/model glm-4.7
```

## `/new`

Starts a new session.

## `/sessions`

Lists previous sessions.

## `/resume`

Resumes session by id or from list.

## `/diff`

Shows latest patch/diff.

## `/clear`

Clears visible terminal conversation, without deleting session history.

## `/exit`

Exits safely.

---

# 17. Message Model

If the current project does not have a clean message model, introduce one.

Minimum model:

```csharp
public enum ChatMessageRole
{
    System,
    User,
    Assistant,
    Tool,
    Error,
    Status
}

public sealed class ChatMessage
{
    public string Id { get; init; }
    public ChatMessageRole Role { get; init; }
    public string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<MessagePart> Parts { get; init; }
}
```

Message parts:

```csharp
public abstract record MessagePart;

public sealed record TextPart(string Text) : MessagePart;

public sealed record CodeBlockPart(
    string Language,
    string Code
) : MessagePart;

public sealed record ToolCallPart(
    string ToolName,
    string ArgumentsPreview,
    string Status
) : MessagePart;

public sealed record ToolResultPart(
    string ToolName,
    bool IsSuccess,
    string Summary,
    string? FullText
) : MessagePart;

public sealed record DiffPart(
    string FilePath,
    string UnifiedDiff
) : MessagePart;
```

Do not force the TUI to parse raw provider JSON directly.

Provider/tool events should be converted into internal agent events first.

---

# 18. Agent Event Stream

The TUI should render events from the agent runtime.

Introduce an event stream if it does not already exist.

Example:

```csharp
public abstract record AgentEvent;

public sealed record AssistantTextDelta(string Text) : AgentEvent;

public sealed record AssistantMessageCompleted(string MessageId) : AgentEvent;

public sealed record ToolCallStarted(
    string ToolCallId,
    string ToolName,
    string ArgumentsPreview
) : AgentEvent;

public sealed record ToolCallCompleted(
    string ToolCallId,
    string ToolName,
    bool Success,
    string Summary,
    string? FullOutput
) : AgentEvent;

public sealed record ApprovalRequested(
    string ApprovalId,
    string ActionName,
    string Summary,
    string? Diff
) : AgentEvent;

public sealed record PatchProposed(
    string FilePath,
    string Diff
) : AgentEvent;

public sealed record AgentStatusChanged(string Status) : AgentEvent;

public sealed record AgentError(string Message, Exception? Exception) : AgentEvent;
```

The TUI should subscribe to this stream and update the UI incrementally.

This is critical because the user must see the process, not only the final answer.

---

# 19. Architecture

Use layered structure.

Suggested folders:

```text
src/
  EvoLoop.Cli/
    Program.cs
    CliOptions.cs

  EvoLoop.Tui/
    TuiApp.cs
    TuiHost.cs
    Screens/
      ChatScreen.cs
      SessionListScreen.cs
      DiffScreen.cs
      ApprovalDialog.cs
    Rendering/
      MessageRenderer.cs
      MarkdownRenderer.cs
      DiffRenderer.cs
      ToolRenderer.cs
      StatusRenderer.cs
    Input/
      InputBox.cs
      SlashCommandPopup.cs
      InputHistory.cs
      KeyBindings.cs
    Commands/
      ISlashCommand.cs
      SlashCommandRegistry.cs
      HelpCommand.cs
      StatusCommand.cs
      ToolsCommand.cs
      ModelCommand.cs
      NewSessionCommand.cs
      SessionsCommand.cs
      ResumeCommand.cs
      DiffCommand.cs
      ClearCommand.cs
      ExitCommand.cs
    Theme/
      TuiTheme.cs

  EvoLoop.Agent/
    AgentRuntime.cs
    AgentEvent.cs
    AgentSession.cs
    Approval/
    Tools/

  EvoLoop.Storage/
    JsonSessionStore.cs
```

If the existing project has different names, adapt to the current structure instead of blindly creating new projects.

---

# 20. TUI Backend

Use the existing TUI dependency if already present.

If `Terminal.Gui` is already vendored or referenced, use it.

If not, first create an abstraction:

```csharp
public interface ITuiBackend
{
    Task RunAsync(TuiApp app, CancellationToken ct);
}
```

Then implement the first backend using `Terminal.Gui`.

Do not couple agent runtime directly to `Terminal.Gui` types.

---

# 21. Rendering Rules

## 21.1 Assistant Messages

Render normal text with wrapping.

Code blocks should be visually separated.

Example:

````text
```csharp
public class Enemy {}
```
````

If markdown parsing is too much for the first version, implement simple fenced-code detection.

## 21.2 Tool Calls

Do not show huge raw JSON by default.

Show compact summary first.

Allow expanding full output later.

## 21.3 Errors

Errors must be visible and actionable.

Bad:

```text
error
```

Good:

```text
✗ build failed
  src/Game.cs(42,13): CS0103 enemySpeed does not exist
  Suggested next step: inspect Game.cs around line 42
```

## 21.4 Long Output

Long outputs should be collapsed or summarized.

Example:

```text
▸ build
  failed with 18 errors
  showing first 5 errors
```

## 21.5 Status Bar

Always show:
- current mode: idle / generating / running tool / waiting approval / error;
- model;
- workspace;
- session;
- approval mode.

---

# 22. Approval Modes

Support these modes:

```csharp
public enum ApprovalMode
{
    ReadOnly,
    AskBeforeWrite,
    AskBeforeCommand,
    AutoApproveWorkspaceWrites
}
```

Default should be safe:

```text
AskBeforeWrite
```

Never auto-approve destructive actions by default.

---

# 23. Implementation Phases

## Phase 1: Repository Audit

Before coding, inspect the repository and write:

```text
docs/TUI_AUDIT.md
docs/TUI_DEPENDENCY_AUDIT.md
```

`docs/TUI_AUDIT.md` must answer:

1. What is the current CLI entry point?
2. How is the agent runtime invoked?
3. Does the project already support streaming?
4. How are tools represented?
5. How are tool calls parsed?
6. How are sessions stored?
7. Is there already an approval system?
8. Which TUI/console dependencies already exist?
9. What is the minimal safe integration path?

`docs/TUI_DEPENDENCY_AUDIT.md` must answer:

1. Is any TUI/console library already referenced?
2. Is `Terminal.Gui` already present?
3. Are any local `.nupkg` packages already vendored?
4. Is there a `NuGet.Config`?
5. Is package restore currently online-only?
6. Does the project have `packages.lock.json`?
7. Which dependencies are required for the first TUI version?
8. Which transitive dependencies must be vendored?
9. Can the project build with `dotnet restore --locked-mode` using only `vendor/nuget`?
10. If not, what is missing?

Do not implement before these audits are written.

## Phase 2: Minimal TUI Shell

Implement:
- app start;
- chat screen;
- input box;
- static message rendering;
- `/help`;
- `/exit`;
- status bar.

The TUI should compile and run even before full agent integration.

## Phase 3: Agent Integration

Connect chat input to existing agent runtime.

Implement:
- sending user message;
- receiving assistant response;
- rendering streaming text if supported;
- cancellation with Ctrl+C;
- error rendering.

If streaming is not supported yet, simulate event-based rendering with completed messages, but keep the event abstraction.

## Phase 4: Tool Event Rendering

Render:
- tool call started;
- tool call completed;
- tool errors;
- file read summaries;
- command summaries;
- patch summaries.

Do not dump raw JSON unless user explicitly opens detailed view.

## Phase 5: Slash Command System

Implement:
- command registry;
- suggestions popup;
- filtering by typed prefix;
- command execution;
- `/status`;
- `/tools`;
- `/model`;
- `/clear`.

## Phase 6: Sessions

Implement:
- current session display;
- `/new`;
- `/sessions`;
- `/resume`;
- JSON session persistence if missing.

## Phase 7: Diffs and Approvals

Implement:
- patch proposal rendering;
- `/diff`;
- approval dialog;
- keyboard approval;
- safe cancellation.

## Phase 8: Polish

Improve:
- colors/theme;
- message spacing;
- scroll behavior;
- input history;
- small terminal handling;
- useful empty states;
- better error summaries.

---

# 24. Non-Goals for First Version

Do not implement yet:
- mouse-heavy UI;
- complex split panes;
- plugin marketplace;
- remote sync;
- cloud accounts;
- full markdown engine if too expensive;
- full-screen code editor;
- complex vim/emacs input modes;
- automatic dependency download;
- web UI.

Keep first version robust and useful.

---

# 25. Quality Requirements

The implementation must:

1. Keep TUI separate from agent runtime.
2. Avoid raw provider/tool JSON in the UI.
3. Render agent process step by step.
4. Support cancellation.
5. Fail gracefully.
6. Work in restricted terminal environments.
7. Use vendored offline-restorable dependencies.
8. Preserve existing behavior where possible.
9. Avoid large rewrites.
10. Add documentation for usage and architecture.

---

# 26. Testing Requirements

Add tests where practical.

Useful tests:

```text
SlashCommandRegistryTests
MessageRendererTests
DiffRendererTests
AgentEventMappingTests
SessionStoreTests
```

At minimum test:
- slash command filtering;
- command lookup;
- markdown/code block parsing;
- diff rendering input parsing;
- event-to-message conversion.

Do not run expensive builds repeatedly unless necessary.

---

# 27. Documentation to Produce

Create or update:

```text
docs/TUI_AUDIT.md
docs/TUI_DEPENDENCY_AUDIT.md
docs/TUI_SPEC.md
docs/TUI_USAGE.md
docs/TUI_ARCHITECTURE.md
docs/DEPENDENCIES.md
vendor/nuget/README.md
```

`TUI_USAGE.md` should explain:
- how to start TUI;
- available slash commands;
- keyboard shortcuts;
- approval modes;
- known limitations.

---

# 28. Expected CLI

Target command examples:

```bash
evoloop tui
evoloop tui --workspace .
evoloop tui --model qwen3-coder
evoloop tui --session latest
evoloop tui --approval ask-before-write
```

If the current CLI shape is different, adapt naturally.

---

# 29. Final Deliverable

After implementation, provide a concise summary:

1. What files were added.
2. What files were changed.
3. How to run the TUI.
4. Which slash commands work.
5. Which dependencies were added.
6. Whether offline restore works.
7. What remains for later.
8. Any limitations or risks.

Do not claim features are complete unless they are implemented and tested.

---

# 30. Important Implementation Style

Prefer simple, readable, idiomatic C#.

Avoid overengineering.

Do not put all logic into `Program.cs`.

Do not mix:
- provider API parsing;
- agent runtime;
- TUI rendering;
- slash command execution;
- storage;
- dependency restore logic.

Keep boundaries clean.

Use cancellation tokens.

Use async APIs.

Handle exceptions and show useful errors in TUI.

The user must always understand what the agent is doing.

---

# 31. Codex App Starting Prompt

Use this prompt in Codex App:

```text
Read this file and implement the TUI in phases.

Important:
External .NET dependencies are allowed, but only as vendored local NuGet packages under vendor/nuget. Do not require online restore from nuget.org. Prefer Terminal.Gui for the main Claude Code-like TUI. Use Spectre.Console only if clearly justified.

Start with audit only:
1. Inspect the current repository.
2. Identify current CLI, agent runtime, tools, sessions, streaming, approval flow, and existing dependencies.
3. Check whether Terminal.Gui or any TUI dependency is already present.
4. Check whether NuGet.Config, packages.lock.json, Directory.Packages.props, or vendor/nuget exist.
5. Propose the safest minimal integration path.
6. Write docs/TUI_AUDIT.md and docs/TUI_DEPENDENCY_AUDIT.md.

Do not implement the TUI until the audit is complete.
Do not add online-only dependencies.
Do not rewrite the whole project.
Keep changes incremental and architecture clean.
```

---

# 32. Suggested Follow-Up Prompt After Audit

After Codex creates the audit files, use:

```text
Now implement Phase 2 only: Minimal TUI Shell.

Requirements:
- Use the dependency strategy from docs/TUI_DEPENDENCY_AUDIT.md.
- If Terminal.Gui is available in vendor/nuget, use it.
- If not, first prepare the local NuGet setup and document what package files are needed.
- Implement app start, chat screen, input box, static message rendering, /help, /exit, and status bar.
- Keep TUI separate from agent runtime.
- Do not implement full agent integration yet.
- Do not rewrite existing runtime code.
- Update docs/TUI_USAGE.md with how to run the minimal TUI.
```
