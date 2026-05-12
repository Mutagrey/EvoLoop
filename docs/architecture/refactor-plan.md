# Refactor Plan

This plan is ordered for small, low-risk cleanup batches. Do not start feature expansion before the early architecture cleanup is complete.

## Phase 1: Inventory And Dead Code Removal

- [x] Create architecture audit docs.
- [ ] Reconcile stale TUI/release documentation:
  - `docs/STATUS.md` says checked-in Windows bundles are not regenerated for `Agent.Tui`.
  - README/deployment docs describe `agent.cmd` as TUI wrapper.
  - `release/windows` currently tracks old CLI-only bundles.
- [ ] Decide whether tracked Windows binaries remain committed or move to generated artifacts only.
- [ ] Review large historical TUI docs (`docs/EvoLoop_TUI_SPEC.md`, `docs/TUI_SPEC.md`, `docs/TUI_AUDIT.md`) and keep one canonical explanation per topic.
- [ ] Remove only code proven unused by references and tests; no confirmed production dead code was found in this audit.

## Phase 2: Folder And Namespace Cleanup

- [ ] Split `AgentContracts.cs` into cohesive files without behavior changes:
  - runtime contracts
  - tool contracts
  - model contracts
  - storage/event contracts
  - config records
  - null implementations
- [ ] Keep public types/names stable during the split.
- [ ] Consider folders inside existing projects before creating new assemblies.
- [ ] Keep `Program.cs` thin in both CLI and TUI.

## Phase 3: Agent Runtime Separation

- [ ] Extract one-session lifecycle or one-step execution helpers from `ReActAgentLoop`.
- [ ] Keep termination conditions explicit and covered by tests.
- [ ] Move deterministic recovery and path-hint scanning behind focused runtime collaborators.
- [ ] Keep model profile switching and invalid-response thresholds behavior-compatible.
- [ ] Preserve normalized internal message flow.

## Phase 4: Tool System Cleanup

- [ ] Keep `ToolCatalog.CreateDefaultTools()` as the single default registry path until real configurability is needed.
- [ ] Centralize workspace path scan skip rules used by fallback search and ReAct path hints.
- [ ] Centralize mutation snapshot manifest shape used by patch/undo and snapshot diff.
- [ ] Keep `exec_shell` fallback-only and policy-controlled.
- [ ] Add structured activity metadata to tool results or events so UI does not parse result strings.
- [ ] Do not add new tools during cleanup unless required to replace unsafe shell usage.

## Phase 5: LLM Adapter Cleanup

- [ ] Deduplicate provider fallback helpers in `ModelClientBase`:
  - HTTP success check
  - system prompt fallback
  - response_format fallback
  - JSON-ReAct fallback wrapping
- [ ] Keep OpenAI-compatible native non-streaming and streaming behavior unchanged.
- [ ] Keep custom gateway compatibility unchanged.
- [ ] Add tests for provider message formatting before changing request shapes.

## Phase 6: Prompt And Skill Cleanup

- [ ] Keep `DefaultPromptBuilder` and `DefaultContextBuilder` behavior stable until runtime split is safer.
- [ ] When prompt edits are needed, move prompt fragments into focused renderer/template files.
- [ ] Keep AGENTS/source-of-truth loading centralized.
- [ ] Keep skill progressive disclosure: index first, full `SKILL.md` only after tool read.

## Phase 7: Storage And Session Cleanup

- [ ] Separate JSONL canonical stores from sqlite projection code if the file keeps growing.
- [ ] Preserve JSONL field compatibility.
- [ ] Define event type names in one place.
- [ ] Keep memory project identity best-effort and tolerant of non-writable storage.
- [ ] Prepare future session-tree support as a consumer of the event stream, not a replacement for JSONL.

## Phase 8: UI/TUI Boundary Cleanup

- [ ] Move CLI local degraded review fallback out of `CliSession` into a shared application/hosting service.
- [ ] Keep CLI rendering in CLI and Terminal.Gui rendering in TUI.
- [ ] Add a TUI runtime observer and approval implementation only after event data is structured enough.
- [ ] Do not make `Agent.Tui` depend on `Agent.Cli`.
- [ ] Do not move Terminal.Gui types outside `Agent.Tui`.

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
  - TUI command/transcript behavior
- [ ] Use the documented build-then-run-DLL workaround if `dotnet run --project tests/Agent.Tests` hangs.

## First Implementation Batch Recommendation

Start with a docs/status cleanup batch:

- Align README, deployment docs, and status around current TUI/release bundle reality.
- Decide whether to regenerate or stop tracking stale `release/windows` binaries.
- Do not touch runtime behavior in this batch.

Then do a pure mechanical split of `AgentContracts.cs`, verified by the lightweight test harness.
