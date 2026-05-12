# Status

Ordered refactor work follows `docs/architecture/refactor-plan.md`; this file records completed work, current problems, and non-ordered next improvements.

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
- Added stricter architecture/development guardrails to `AGENTS.md` and `docs/ARCHITECTURE.md`.
- Split CLI session handling out of `Program.cs`.
- Split search tools, rerank cache, search ranking, and safe fallback file enumeration into focused files.
- Shared text/scalar extraction between tool argument reading and ReAct recovery.
- Hardened symlink traversal checks for non-existing write/patch targets.
- Tightened fallback search/path scanning around generated, storage, and binary paths.
- Made Windows release bundle scripts regenerate `agent.cmd` wrappers.
- Split interactive TUI and pure CLI into separate executable targets: `Agent.Tui` and `Agent.Cli`, with shared startup/runtime wiring in `Agent.Hosting`.
- Added TUI audit docs, vendored `Terminal.Gui 1.19.0` package files, and generated current project lock files for offline restore.
- Implemented the minimal `Agent.Tui` Terminal.Gui shell with static transcript, input line, status bar, `/help`, `/exit`, and shutdown shortcuts.
- Added testable TUI command and transcript rendering helpers covered by the lightweight test harness.
- Added a TUI theme layer with `claude-dark` and `mono`; the default dark theme uses gray terminal colors with an amber/yellow workspace path.
- Made user command installers repair existing EvoLoop profile blocks, add `agent-cli`, and keep `agent doctor/run/plan/review/repl` compatible with the CLI target.
- Completed Phase 1 inventory without runtime changes: no production code was proven dead, duplicate implementations are recorded in the architecture audit, and TUI/release docs now identify one canonical source per topic.
- Decided to keep `release/windows` committed for now as generated artifacts for offline/GitHub delivery.
- Completed Phase 2 contract cleanup: split `AgentContracts.cs` into focused runtime, tool, model, storage/event, config, and null implementation files without public type or behavior changes.
- Started Phase 3 runtime separation by moving normal and fatal session completion paths from `ReActAgentLoop.RunAsync` into focused lifecycle helpers, preserving step-loop termination logic.
- Added explicit ReAct termination tests for clarify responses, max-step exhaustion, and repeated final-without-tools replies.
- Moved deterministic ReAct recovery/bootstrap decisions into a focused runtime collaborator while preserving loop behavior.
- Moved ReAct path-hint capture, inference, normalization, and scan rules into a focused runtime collaborator.
- Moved ReAct profile selection, model limits, tool-calling mode lookup, and switch-threshold handling into a focused runtime collaborator.
- Completed Phase 3 runtime separation with centralized normalized message-history append helpers for user, assistant, and tool-result turns.
- Completed Phase 4 tool cleanup: kept `ToolCatalog.CreateDefaultTools()` as the default registry path, centralized workspace scan skip rules, centralized mutation snapshot manifests, and added structured tool activity metadata for UI/event consumers.
- Completed Phase 5 LLM adapter cleanup: provider fallback helpers now live in `ModelClientBase`, OpenAI/custom gateway request compatibility is covered by focused provider payload tests, and native tool-call behavior remains unchanged.
- Completed Phase 6 prompt and skill cleanup without prompt behavior changes: source-of-truth doc loading and progressive skill indexing now live in focused context helpers.
- Completed Phase 7 storage/session cleanup: split canonical JSONL stores from the SQLite projection, centralized event type names, and documented JSONL as the source for future session-tree views.
- Started Phase 8 UI/TUI boundary cleanup by moving local degraded review fallback into `Agent.Hosting` while keeping rendering in CLI/TUI-specific layers.
- Completed Phase 8 UI/TUI boundary cleanup by adding TUI-local runtime observer and approval adapters without making `Agent.Tui` depend on `Agent.Cli` or moving Terminal.Gui types outside TUI.
- Connected `Agent.Tui` input to `AgentTaskRunner`: plain text runs `run`, `/plan <task>` runs read-only plan mode, and `/review [focus]` runs review mode through the shared hosting/runtime path.
- Started Phase 9 safety/test cleanup with focused TUI dispatch tests for plain input, `/plan <task>`, and `/review [focus]`.
- Added a basic Terminal.Gui approve/reject dialog for TUI runtime approval requests.
- Added compact TUI runtime event formatting so tool, approval, model, and session events no longer render as raw enum names.
- Added structured TUI approval previews for file writes and patches, including path plus diff/content preview.
- Hardened patch/write/delete mutation flow so unavailable snapshot storage returns an explicit error before workspace mutation.
- Hardened undo recovery so missing snapshots are detected before replacing the target path and file/directory type mismatches are handled explicitly.
- Added deterministic coverage for local degraded review fallback: review can return snapshot evidence without model/git while normal run stays blocked.
- Added Phase 9 coverage for snapshot diff evidence, missing snapshot manifests, directory deletion review evidence, and review-mode denials for mutations and shell execution.
- Completed Phase 9 safety/test cleanup with config loading, offline-strict override, and local-only degraded-mode coverage.
- Added append-only mutation snapshot history and multi-file `workspace_snapshot_diff` summaries for local review fallback.
- Simplified generated config to a minimal file with one `reasoning` model profile and no default `fast`/`fallback` profiles.
- Replaced hidden model-profile switch ordering with explicit `runtime.profileFallbackOrder`.
- Added Pi-inspired prompt files: global/workspace `SYSTEM.md`, `APPEND_SYSTEM.md`, and indexed workspace prompt templates under `.evoloop/prompts/*.md`.
- Added TUI `/config`, `/config path`, `/config open`, and `/config reload` commands with grouped settings and runtime reload wiring.
- Added structured TUI tool activity rendering for read, edit, search, command, explore, and failed tool completions.
- Improved TUI transcript readability with grouped timestamped messages, role-specific colors, and a thinking/working spinner.
- Added an `ollama` model provider for local `/api/chat` with `think=false`, and verified `qwen3.5:9b` through a read-only `fs_read` agent flow.
- Raised the default ReAct step budget to 120 because JSON-ReAct fallback normally executes one tool per model turn.
- Stopped run scripts from overriding `HOME` so user-level config such as `~/.evoloop-agent/config.json` is visible when launching `agent`.
- Removed `win-arm64` packaging support; Windows distribution is `win-x64` only.
- Switched the default approval mode to `AutoEdit`, allowing normal workspace writes/patches without repeated prompts while keeping destructive actions approval-gated.

## Current Problems

- No packaged Windows smoke test has been executed yet against the new `win-x64` publish path.
- The agent still depends on a remote/local model gateway for autonomous `run` and `plan` execution; `local-only degraded` mode is still diagnostic-safe, not a replacement for a local model runtime.
- `dotnet run --project tests/Agent.Tests/Agent.Tests.csproj` can still hang in this macOS workspace while spawning MSBuild child nodes; build the solution first and run the compiled test DLL directly as documented in `docs/TESTING.md`.
- TUI approval uses a basic blocking dialog; review-specific diff navigation is not implemented yet.

## Next Improvements

- Resolve the executable-project build hang in this macOS workspace and re-run the full test harness.
- Smoke-test the self-contained Windows artifact on a restricted non-admin machine.
- Exercise native tool calling against real corporate OpenAI-compatible gateways in all supported modes.
- Add TUI review-specific diff navigation.
