# Refactor Plan

This plan is ordered for small, low-risk cleanup batches. Do not start feature expansion before the early architecture cleanup is complete.

## Phase 1: Inventory And Dead Code Removal

- [x] Create architecture audit docs.
- [x] Reconcile stale TUI/release documentation:
  - `docs/STATUS.md` says checked-in Windows bundles are not regenerated for `Agent.Tui`.
  - README/deployment docs describe `agent.cmd` as TUI wrapper.
  - `release/windows` currently tracks old CLI-only bundles.
- [x] Decide whether tracked Windows binaries remain committed or move to generated artifacts only.
- [x] Review large historical TUI docs (`docs/EvoLoop_TUI_SPEC.md`, `docs/TUI_SPEC.md`, `docs/TUI_AUDIT.md`) and keep one canonical explanation per topic.
- [x] Remove only code proven unused by references and tests; no confirmed production dead code was found in this audit.

## Phase 2: Folder And Namespace Cleanup

- [x] Split `AgentContracts.cs` into cohesive files without behavior changes:
  - runtime contracts
  - tool contracts
  - model contracts
  - storage/event contracts
  - config records
  - null implementations
- [x] Keep public types/names stable during the split.
- [x] Consider folders inside existing projects before creating new assemblies.
- [x] Keep `Program.cs` thin in both CLI and TUI.

## Phase 3: Agent Runtime Separation

- [x] Extract one-session lifecycle or one-step execution helpers from `ReActAgentLoop`.
- [x] Keep termination conditions explicit and covered by tests.
- [x] Move deterministic recovery and path-hint scanning behind focused runtime collaborators:
  - [x] Move deterministic recovery/bootstrap decisions behind a focused runtime collaborator.
  - [x] Move path-hint capture, inference, and scan rules behind a focused runtime collaborator.
- [x] Keep model profile switching and invalid-response thresholds behavior-compatible.
- [x] Preserve normalized internal message flow.

## Phase 4: Tool System Cleanup

- [x] Keep `ToolCatalog.CreateDefaultTools()` as the single default registry path until real configurability is needed.
- [x] Centralize workspace path scan skip rules used by fallback search and ReAct path hints.
- [x] Centralize mutation snapshot manifest shape used by patch/undo and snapshot diff.
- [x] Keep `exec_shell` fallback-only and policy-controlled.
- [x] Add structured activity metadata to tool results or events so UI does not parse result strings.
- [x] Do not add new tools during cleanup unless required to replace unsafe shell usage.

## Phase 5: LLM Adapter Cleanup

- [x] Deduplicate provider fallback helpers in `ModelClientBase`:
  - HTTP success check
  - system prompt fallback
  - response_format fallback
  - JSON-ReAct fallback wrapping
- [x] Keep OpenAI-compatible native non-streaming and streaming behavior unchanged.
- [x] Keep custom gateway compatibility unchanged.
- [x] Add tests for provider message formatting before changing request shapes.

## Phase 6: Prompt And Skill Cleanup

- [x] Keep `DefaultPromptBuilder` and `DefaultContextBuilder` behavior stable until runtime split is safer.
- [x] No prompt text edits were needed; prompt builder remains unchanged.
- [x] Keep AGENTS/source-of-truth loading centralized.
- [x] Keep skill progressive disclosure: index first, full `SKILL.md` only after tool read.

## Phase 7: Storage And Session Cleanup

- [x] Separate JSONL canonical stores from sqlite projection code if the file keeps growing.
- [x] Preserve JSONL field compatibility.
- [x] Define event type names in one place.
- [x] Keep memory project identity best-effort and tolerant of non-writable storage.
- [x] Prepare future session-tree support as a consumer of the event stream, not a replacement for JSONL.

## Phase 8: UI/TUI Boundary Cleanup

- [x] Move CLI local degraded review fallback out of `CliSession` into a shared application/hosting service.
- [x] Keep CLI rendering in CLI and Terminal.Gui rendering in TUI.
- [x] Add a TUI runtime observer and approval implementation only after event data is structured enough.
- [x] Do not make `Agent.Tui` depend on `Agent.Cli`.
- [x] Do not move Terminal.Gui types outside `Agent.Tui`.

## Phase 9: Tests And Safety Checks

- [ ] Preserve current lightweight in-repo harness.
- [ ] Keep/add tests for:
  - path safety and symlink traversal
  - patch/undo and snapshot diff
  - plan/review mode denials
  - shell command policy
  - JSON-ReAct and plain-text recovery
  - native non-streaming and streaming tool calls
  - config loading and degraded mode
  - provider formatting/fallback behavior
  - [x] TUI command/transcript behavior
- [ ] Use the documented build-then-run-DLL workaround if `dotnet run --project tests/Agent.Tests` hangs.

## Completed Early Batches

- Docs/status cleanup aligned README, deployment docs, status, and release bundle notes around current TUI/release reality.
- `release/windows` remains tracked as a generated-artifact area for now.
- `AgentContracts.cs` was split mechanically into focused files and verified with a solution build.

No runtime behavior changes were intended in these batches.
