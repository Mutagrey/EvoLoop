using System.Text.Json;

namespace Agent.Core;

public sealed partial class ReActAgentLoop : IAgentLoop
{
    private readonly IModelClientRouter _modelRouter;
    private readonly IModelAdapterRouter _modelAdapterRouter;
    private readonly IPolicyEngine _policyEngine;
    private readonly IApprovalService _approvalService;
    private readonly IEventStore _eventStore;
    private readonly IWorkspaceMemoryStore _memoryStore;
    private readonly IToolContextFactory _contextFactory;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly AgentConfig _config;
    private readonly IContextBuilder _contextBuilder;
    private readonly IPromptBuilder _promptBuilder;
    private readonly IToolTurnExecutor _toolTurnExecutor;

    public ReActAgentLoop(
        IModelClientRouter modelRouter,
        IEnumerable<ITool> tools,
        IPolicyEngine policyEngine,
        IApprovalService approvalService,
        IEventStore eventStore,
        IToolContextFactory contextFactory,
        AgentConfig config,
        IWorkspaceMemoryStore? memoryStore = null,
        IContextBuilder? contextBuilder = null,
        IPromptBuilder? promptBuilder = null,
        IToolTurnExecutor? toolTurnExecutor = null,
        IModelAdapterRouter? modelAdapterRouter = null)
    {
        _modelRouter = modelRouter;
        _modelAdapterRouter = modelAdapterRouter ?? (modelRouter as IModelAdapterRouter) ?? new ModelClientAdapterRouter(modelRouter);
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _policyEngine = policyEngine;
        _approvalService = approvalService;
        _eventStore = eventStore;
        _memoryStore = memoryStore ?? NullWorkspaceMemoryStore.Instance;
        _contextFactory = contextFactory;
        _config = config;
        _contextBuilder = contextBuilder ?? new DefaultContextBuilder();
        _promptBuilder = promptBuilder ?? new DefaultPromptBuilder();
        _toolTurnExecutor = toolTurnExecutor ?? new DefaultToolTurnExecutor();
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var observer = request.Observer ?? NullObserver.Instance;
        var session = await _eventStore.StartSessionAsync(request.WorkspaceRoot, request.ProfileName, request.Task, ct);
        var trace = new List<SessionStep>();
        var maxSteps = request.MaxSteps.GetValueOrDefault(_config.Runtime.MaxSteps);

        await observer.OnEventAsync(new AgentRunEvent(
            AgentRunEventType.SessionStarted,
            $"Session {session.SessionId} started",
            null,
            null), ct);

        var profilePlan = BuildProfilePlan(request.ProfileName);
        var profileIndex = 0;
        var currentProfileName = profilePlan[profileIndex];
        var context = _contextFactory.Create(
            request.WorkspaceRoot,
            session.SessionId,
            currentProfileName,
            request.ExecutionMode,
            request.ApprovalMode);
        await context.EventLog.AppendAsync(new AgentEventRecord(
            session.SessionId,
            "session_start",
            DateTimeOffset.UtcNow,
            request.Task,
            null,
            null,
            new Dictionary<string, string>
            {
                ["profile"] = currentProfileName,
                ["execution_mode"] = request.ExecutionMode.ToString(),
                ["approval_mode"] = request.ApprovalMode.ToString()
            }), ct);
        var modelName = _modelRouter.ResolveModelName(currentProfileName);
        var toolCallingMode = ResolveToolCallingMode(currentProfileName);
        var modelAdapter = _modelAdapterRouter.GetAdapter(currentProfileName, toolCallingMode);

        var history = (await _contextBuilder.BuildInitialMessagesAsync(request, context, _memoryStore, ct)).ToList();
        var internalHistory = history.Select(ToInternalMessage).ToList();

        var requiresToolBeforeFinal = TaskLikelyRequiresTools(request.Task);
        var toolStepsExecuted = 0;
        var consecutiveInvalidResponses = 0;
        var consecutiveFinalWithoutTools = 0;
        var lastModelIssue = string.Empty;
        var contextCompactions = 0;
        var pathHints = new List<string>();
        var pendingToolTurns = new Queue<PendingToolTurn>();

        string finalMessage = "Agent ended without final answer.";
        bool success = false;

        try
        {
            for (var step = 1; step <= maxSteps; step++)
            {
                AgentDecision decision;
                AssistantMessage assistantMessage;
                string modelContent;
                ModelAdapterTurnResult modelResult;
                var appendAssistantToHistory = true;

                if (pendingToolTurns.Count > 0)
                {
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ToolDecision,
                        $"Continuing queued tool call from previous model turn ({pendingToolTurns.Count} remaining).",
                        step), ct);

                    var pending = pendingToolTurns.Dequeue();
                    decision = pending.Decision;
                    assistantMessage = pending.AssistantMessage;
                    modelContent = pending.ModelContent;
                    modelResult = new ModelAdapterTurnResult(
                        assistantMessage,
                        modelName,
                        Raw: assistantMessage.RawContent,
                        ToolCallingMode: pending.ToolCallingMode);
                    appendAssistantToHistory = false;
                }
                else
                {
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelCallStarted,
                        $"Step {step}/{maxSteps}: analyzing with profile '{currentProfileName}'",
                        step), ct);

                    if (TryCompactHistory(history, _config.Runtime, out var compactedMessage))
                    {
                        contextCompactions++;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ContextCompacted,
                            $"Context compacted (pass {contextCompactions}). {compactedMessage}",
                            step), ct);
                    }

                    var adaptiveDirective = _config.Runtime.AdaptivePromptingEnabled
                        ? BuildAdaptiveDirective(
                            requiresToolBeforeFinal,
                            toolStepsExecuted,
                            consecutiveInvalidResponses,
                            consecutiveFinalWithoutTools,
                            lastModelIssue)
                        : string.Empty;
                    var systemPrompt = _promptBuilder.BuildSystemPrompt(_tools.Values.ToList(), context);
                    if (!string.IsNullOrWhiteSpace(adaptiveDirective))
                    {
                        systemPrompt += "\n\nADAPTIVE EXECUTION DIRECTIVE:\n" + adaptiveDirective;
                    }

                    toolCallingMode = ResolveToolCallingMode(currentProfileName);
                    modelAdapter = _modelAdapterRouter.GetAdapter(currentProfileName, toolCallingMode);
                    var modelRequest = new ModelAdapterTurnRequest(
                        currentProfileName,
                        modelName,
                        systemPrompt,
                        history,
                        internalHistory,
                        _tools.Values.ToList(),
                        toolCallingMode,
                        GetTemperature(currentProfileName),
                        GetMaxTokens(currentProfileName),
                        new Dictionary<string, string>
                        {
                            ["session_id"] = session.SessionId,
                            ["step"] = step.ToString(),
                            ["profile"] = currentProfileName
                        });

                    await context.EventLog.AppendAsync(new AgentEventRecord(
                        session.SessionId,
                        "llm_request",
                        DateTimeOffset.UtcNow,
                        $"profile={currentProfileName}; step={step}",
                        null,
                        null,
                        new Dictionary<string, string>
                        {
                            ["model"] = modelName
                        }), ct);

                    modelResult = await modelAdapter.CompleteTurnAsync(modelRequest, ct);

                    await observer.OnEventAsync(new AgentRunEvent(AgentRunEventType.ModelCallCompleted, "Model response received", step), ct);

                    assistantMessage = modelResult.AssistantMessage;
                    modelContent = assistantMessage.RawContent ?? AssistantToLegacyContent(assistantMessage);
                    decision = DecisionFromAssistantMessage(assistantMessage);
                    EnqueueRemainingToolCalls(pendingToolTurns, assistantMessage, modelContent, modelResult.ToolCallingMode);
                }

                if (decision.Type == AgentDecisionType.Invalid)
                {
                    if (!requiresToolBeforeFinal && TryRecoverPlainFinalMessage(modelContent, out var recoveredFinal))
                    {
                        finalMessage = recoveredFinal;
                        success = true;
                        break;
                    }

                    consecutiveInvalidResponses++;
                    lastModelIssue = "response_format_invalid";
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelResponseInvalid,
                        $"Model response format invalid at step {step}.",
                        step), ct);

                    var bootstrapDecision = TryCreateBootstrapDecision(
                        requiresToolBeforeFinal,
                        toolStepsExecuted,
                        consecutiveInvalidResponses,
                        request.Task);
                    if (bootstrapDecision is not null)
                    {
                        decision = bootstrapDecision;
                        consecutiveInvalidResponses = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelDecisionRecovered,
                            $"Recovered by deterministic bootstrap tool '{decision.ToolName}'.",
                            step,
                            decision.ToolName), ct);
                    }
                    else
                    {
                        if (consecutiveInvalidResponses >= GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                            TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                        {
                            consecutiveInvalidResponses = 0;
                            consecutiveFinalWithoutTools = 0;
                            await observer.OnEventAsync(new AgentRunEvent(
                                AgentRunEventType.ModelProfileSwitched,
                                $"Switched model profile to '{currentProfileName}' after repeated invalid responses.",
                                step), ct);
                            history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. Continue with strict JSON tool decisions."));
                            internalHistory.Add(new UserMessage(history[^1].Content));
                            continue;
                        }

                        if (consecutiveInvalidResponses >= _config.Runtime.MaxInvalidModelResponses)
                        {
                            finalMessage = $"Stopped after {consecutiveInvalidResponses} invalid model responses. Model must return strict JSON tool/final schema.";
                            success = false;
                            break;
                        }

                        if (appendAssistantToHistory)
                        {
                            AppendAssistantHistory(history, internalHistory, assistantMessage, modelContent);
                        }
                        history.Add(new ModelMessage(
                            "user",
                            "OBSERVATION: Response format invalid. Return EXACTLY one JSON object using one of schemas: " +
                            "{\"type\":\"tool\",\"tool\":\"...\",\"reason\":\"...\",\"arguments\":{...}} " +
                            "or {\"type\":\"final\",\"message\":\"...\"} or {\"type\":\"clarify\",\"message\":\"...\"}."));
                        internalHistory.Add(new UserMessage(history[^1].Content));
                        continue;
                    }
                }

                if (decision.Type == AgentDecisionType.Tool &&
                    decision.Reason.Contains("recovered", StringComparison.OrdinalIgnoreCase))
                {
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelDecisionRecovered,
                        $"Recovered model decision to tool '{decision.ToolName}'.",
                        step,
                        decision.ToolName), ct);
                }

                if (decision.Type == AgentDecisionType.Final)
                {
                    consecutiveInvalidResponses = 0;
                    if (requiresToolBeforeFinal && toolStepsExecuted == 0 && _tools.Count > 0)
                    {
                        consecutiveFinalWithoutTools++;
                        lastModelIssue = "final_without_required_tools";
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.FinalRejectedRequiresTool,
                            $"Final rejected at step {step}: task requires tool actions first.",
                            step), ct);

                        var deterministicBootstrap = TryCreateBootstrapDecision(
                            requiresToolBeforeFinal,
                            toolStepsExecuted,
                            1,
                            request.Task);
                        if (deterministicBootstrap is not null)
                        {
                            decision = deterministicBootstrap;
                            consecutiveFinalWithoutTools = 0;
                            await observer.OnEventAsync(new AgentRunEvent(
                                AgentRunEventType.ModelDecisionRecovered,
                                $"Recovered by deterministic bootstrap tool '{decision.ToolName}' after final-without-tools.",
                                step,
                                decision.ToolName), ct);
                        }
                        else
                        {
                            if (consecutiveFinalWithoutTools >= GetSwitchThreshold(_config.Runtime.FinalWithoutToolsBeforeProfileSwitch) &&
                                TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                            {
                                consecutiveInvalidResponses = 0;
                                consecutiveFinalWithoutTools = 0;
                                await observer.OnEventAsync(new AgentRunEvent(
                                    AgentRunEventType.ModelProfileSwitched,
                                    $"Switched model profile to '{currentProfileName}' after repeated final-without-tools replies.",
                                    step), ct);
                                history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. You must use tools for this task."));
                                internalHistory.Add(new UserMessage(history[^1].Content));
                                continue;
                            }

                            if (consecutiveFinalWithoutTools >= _config.Runtime.MaxConsecutiveFinalWithoutTools)
                            {
                                finalMessage = $"Stopped after {consecutiveFinalWithoutTools} final-only replies without any tool calls for an action-oriented task.";
                                success = false;
                                break;
                            }

                            if (appendAssistantToHistory)
                            {
                                AppendAssistantHistory(history, internalHistory, assistantMessage, modelContent);
                            }
                            history.Add(new ModelMessage(
                                "user",
                                "OBSERVATION: This task requires workspace actions. Call an appropriate tool before returning final."));
                            internalHistory.Add(new UserMessage(history[^1].Content));
                            continue;
                        }
                    }
                    else
                    {
                        consecutiveFinalWithoutTools = 0;
                        lastModelIssue = string.Empty;
                        finalMessage = decision.Message;
                        success = true;
                        break;
                    }
                }

                if (decision.Type == AgentDecisionType.Clarify)
                {
                    consecutiveInvalidResponses = 0;
                    finalMessage = decision.Message;
                    success = false;
                    break;
                }

                if (string.IsNullOrWhiteSpace(decision.ToolName) || !_tools.TryGetValue(decision.ToolName, out var tool))
                {
                    var invalidTool = decision.ToolName ?? "<none>";
                    consecutiveInvalidResponses++;
                    lastModelIssue = "unknown_tool";
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelResponseInvalid,
                        $"Unknown tool '{invalidTool}' at step {step}.",
                        step), ct);

                    if (consecutiveInvalidResponses >= GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                        TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                    {
                        consecutiveInvalidResponses = 0;
                        consecutiveFinalWithoutTools = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelProfileSwitched,
                            $"Switched model profile to '{currentProfileName}' after repeated unknown tool decisions.",
                            step), ct);
                        history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. Use only allowed tools: {string.Join(", ", _tools.Keys.OrderBy(x => x))}."));
                        internalHistory.Add(new UserMessage(history[^1].Content));
                        continue;
                    }

                    if (consecutiveInvalidResponses >= _config.Runtime.MaxInvalidModelResponses)
                    {
                        finalMessage = $"Stopped after {consecutiveInvalidResponses} invalid model decisions (format/unknown tool).";
                        success = false;
                        break;
                    }

                    if (appendAssistantToHistory)
                    {
                        AppendAssistantHistory(history, internalHistory, assistantMessage, modelContent);
                    }
                    history.Add(new ModelMessage("user", $"OBSERVATION: Unknown tool '{invalidTool}'. Use one of: {string.Join(", ", _tools.Keys.OrderBy(x => x))}."));
                    internalHistory.Add(new UserMessage(history[^1].Content));
                    continue;
                }

                if (TryRepairToolDecision(
                    tool.Name,
                    decision,
                    request.Task,
                    request.WorkspaceRoot,
                    pathHints,
                    modelContent,
                    out var repairedDecision,
                    out var repairNote))
                {
                    decision = repairedDecision;
                    if (!string.IsNullOrWhiteSpace(repairNote))
                    {
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelDecisionRecovered,
                            repairNote,
                            step,
                            tool.Name), ct);
                    }
                }

                var validationErrors = ToolArgumentValidator.Validate(tool, decision.Arguments);
                var missingRequired = validationErrors
                    .Where(error => error.StartsWith("missing_required:", StringComparison.OrdinalIgnoreCase))
                    .Select(error => error["missing_required:".Length..])
                    .ToArray();
                if (missingRequired.Length > 0 &&
                    TryBuildDeterministicRecoveryDecision(
                        tool.Name,
                        missingRequired,
                        decision,
                        request.Task,
                        request.WorkspaceRoot,
                        pathHints,
                        out var recoveryDecision,
                        out var recoveryNote) &&
                    _tools.TryGetValue(recoveryDecision.ToolName, out var recoveredTool))
                {
                    decision = recoveryDecision;
                    tool = recoveredTool;
                    validationErrors = ToolArgumentValidator.Validate(tool, decision.Arguments);
                    missingRequired = validationErrors
                        .Where(error => error.StartsWith("missing_required:", StringComparison.OrdinalIgnoreCase))
                        .Select(error => error["missing_required:".Length..])
                        .ToArray();
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelDecisionRecovered,
                        recoveryNote,
                        step,
                        tool.Name), ct);
                }

                if (validationErrors.Count > 0)
                {
                    consecutiveInvalidResponses++;
                    lastModelIssue = $"missing_required_arguments:{tool.Name}";
                    var missing = string.Join(", ", validationErrors);
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelResponseInvalid,
                        $"Tool '{tool.Name}' failed argument validation: {missing}",
                        step,
                        tool.Name), ct);

                    if (consecutiveInvalidResponses >= GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                        TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                    {
                        consecutiveInvalidResponses = 0;
                        consecutiveFinalWithoutTools = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelProfileSwitched,
                            $"Switched model profile to '{currentProfileName}' after repeated argument-validation failures.",
                            step), ct);
                        history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. Tool '{tool.Name}' validation failed: {missing}."));
                        internalHistory.Add(new UserMessage(history[^1].Content));
                        continue;
                    }

                    if (consecutiveInvalidResponses >= _config.Runtime.MaxInvalidModelResponses)
                    {
                        finalMessage = $"Stopped after {consecutiveInvalidResponses} invalid model decisions (format/unknown tool/missing arguments).";
                        success = false;
                        break;
                    }

                    var requiredHint = string.Join(", ", tool.Schema.RequiredFields);
                    if (appendAssistantToHistory)
                    {
                        AppendAssistantHistory(history, internalHistory, assistantMessage, modelContent);
                    }
                    history.Add(new ModelMessage(
                        "user",
                        $"OBSERVATION: Tool '{tool.Name}' call is invalid. Validation errors: {missing}. " +
                        $"Return STRICT JSON tool call and include required fields: {requiredHint}."));
                    internalHistory.Add(new UserMessage(history[^1].Content));
                    continue;
                }

                consecutiveInvalidResponses = 0;
                consecutiveFinalWithoutTools = 0;
                lastModelIssue = string.Empty;

                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ToolDecision,
                    BuildToolPlanMessage(tool.Name, decision.Reason),
                    step,
                    tool.Name), ct);

                var call = new ToolCall(tool.Name, decision.Arguments, decision.Reason);
                var turnResult = await _toolTurnExecutor.ExecuteAsync(new ToolExecutionRequest(
                    tool,
                    call,
                    context,
                    step,
                    "tool",
                    decision.Reason,
                    _policyEngine,
                    _approvalService,
                    _eventStore,
                    observer), ct);

                if (appendAssistantToHistory)
                {
                    AppendAssistantHistory(history, internalHistory, assistantMessage, modelContent);
                }
                if (!turnResult.Executed)
                {
                    lastModelIssue = "policy_or_approval_blocked";
                    var blockedObservation = turnResult.ObservationMessage ?? "OBSERVATION: Tool execution was blocked.";
                    history.Add(new ModelMessage("user", blockedObservation));
                    internalHistory.Add(CreateToolResultMessage(
                        decision,
                        new ToolResult(false, blockedObservation),
                        _config.Runtime.ObservationMaxChars));
                    if (request.ExecutionMode is AgentExecutionMode.Plan or AgentExecutionMode.Review)
                    {
                        toolStepsExecuted++;
                    }
                    continue;
                }

                var result = turnResult.Result!;
                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ToolExecutionCompleted,
                    BuildToolCompletionMessage(tool.Name, decision.Arguments, result),
                    step,
                    tool.Name), ct);

                CapturePathHints(pathHints, request.WorkspaceRoot, tool.Name, decision.Arguments, result);
                trace.Add(turnResult.Step!);
                toolStepsExecuted++;
                lastModelIssue = result.Success ? string.Empty : $"tool_failed:{tool.Name}";
                var observation = BuildObservationMessage(tool.Name, result, _config.Runtime.ObservationMaxChars);
                var toolResultMessage = CreateToolResultMessage(decision, result, _config.Runtime.ObservationMaxChars);
                if (IsNativeToolMode(modelResult.ToolCallingMode))
                {
                    history.Add(new ModelMessage("tool", toolResultMessage.ToObservationText(_config.Runtime.ObservationMaxChars), decision.ToolCallId, tool.Name));
                }
                else
                {
                    history.Add(new ModelMessage("user", observation));
                }

                internalHistory.Add(toolResultMessage);
            }

            if (!success && finalMessage == "Agent ended without final answer.")
            {
                finalMessage = $"Reached max steps ({maxSteps}) without final answer.";
            }

            await _eventStore.CompleteSessionAsync(session.SessionId, success ? "completed" : "incomplete", ct);

            if (_config.Runtime.MemoryEnabled)
            {
                try
                {
                    await _memoryStore.SaveRunAsync(new WorkspaceMemoryRecord(
                        request.WorkspaceRoot,
                        session.SessionId,
                        request.Task,
                        success,
                        finalMessage,
                        trace,
                        DateTimeOffset.UtcNow), ct);

                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.MemoryUpdated,
                        "Workspace memory updated."), ct);
                }
                catch (Exception ex)
                {
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.MemoryUpdated,
                        $"Workspace memory update skipped: {ToOneLine(ex.Message, 120)}"), ct);
                }
            }

            await observer.OnEventAsync(new AgentRunEvent(
                AgentRunEventType.SessionCompleted,
                success ? "Task completed" : "Task ended without completion"), ct);

            await context.EventLog.AppendAsync(new AgentEventRecord(
                session.SessionId,
                "final_answer",
                DateTimeOffset.UtcNow,
                finalMessage,
                null,
                success), ct);
            await context.EventLog.AppendAsync(new AgentEventRecord(
                session.SessionId,
                "session_end",
                DateTimeOffset.UtcNow,
                success ? "completed" : "incomplete",
                null,
                success), ct);

            return new AgentRunResult(success, finalMessage, trace.Count, session.SessionId, trace);
        }
        catch (Exception ex)
        {
            await _eventStore.CompleteSessionAsync(session.SessionId, "error", ct);
            await context.EventLog.AppendAsync(new AgentEventRecord(
                session.SessionId,
                "session_end",
                DateTimeOffset.UtcNow,
                ex.Message,
                null,
                false), ct);

            if (_config.Runtime.MemoryEnabled)
            {
                try
                {
                    await _memoryStore.SaveRunAsync(new WorkspaceMemoryRecord(
                        request.WorkspaceRoot,
                        session.SessionId,
                        request.Task,
                        false,
                        $"Fatal error: {ex.Message}",
                        trace,
                        DateTimeOffset.UtcNow), ct);
                }
                catch
                {
                    // do not override main failure
                }
            }

            await observer.OnEventAsync(new AgentRunEvent(AgentRunEventType.Error, ex.Message), ct);
            return new AgentRunResult(false, $"Fatal error: {ex.Message}", trace.Count, session.SessionId, trace);
        }
    }

    private ToolCallingMode ResolveToolCallingMode(string profileName)
    {
        if (_config.Models.TryGetValue(profileName, out var profile))
        {
            return profile.ToolCallingMode;
        }

        return ToolCallingMode.JsonReActFallback;
    }

    private double GetTemperature(string profileName)
    {
        var raw = _config.Models.TryGetValue(profileName, out var profile) ? profile.Temperature : 0.2;
        var min = _config.Runtime.ModelMinTemperature;
        var max = _config.Runtime.ModelMaxTemperature;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(raw, min, max);
    }

    private int GetMaxTokens(string profileName)
    {
        var raw = _config.Models.TryGetValue(profileName, out var profile) ? profile.MaxTokens : 1200;
        var min = _config.Runtime.ModelMinOutputTokens;
        var max = _config.Runtime.ModelMaxOutputTokens;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(raw, min, max);
    }

    private static bool TaskLikelyRequiresTools(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return false;
        }

        var normalized = task.ToLowerInvariant();
        var keywords = new[]
        {
            "create", "edit", "update", "modify", "delete", "write", "patch",
            "file", "folder", "project", "repository", "repo", "git", "commit",
            "run", "build", "test", "search", "scan", "analyze code", "refactor"
        };

        return keywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal));
    }

    private List<string> BuildProfilePlan(string requestedProfile)
    {
        var ordered = new List<string>();
        void AddIfExists(string profile)
        {
            if (_config.Models.ContainsKey(profile) &&
                !ordered.Contains(profile, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(profile);
            }
        }

        AddIfExists(requestedProfile);
        AddIfExists("reasoning");
        AddIfExists("fallback");
        AddIfExists("fast");

        foreach (var profile in _config.Models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            AddIfExists(profile);
        }

        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("No model profiles configured.");
        }

        return ordered;
    }

    private bool TrySwitchProfile(
        IReadOnlyList<string> profilePlan,
        ref int profileIndex,
        ref string currentProfileName,
        ref string modelName,
        string workspaceRoot,
        string sessionId,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        ref ToolContext context)
    {
        if (profileIndex + 1 >= profilePlan.Count)
        {
            return false;
        }

        profileIndex++;
        currentProfileName = profilePlan[profileIndex];
        modelName = _modelRouter.ResolveModelName(currentProfileName);
        context = _contextFactory.Create(workspaceRoot, sessionId, currentProfileName, executionMode, approvalMode);
        return true;
    }

    private static int GetSwitchThreshold(int rawThreshold)
    {
        return rawThreshold <= 0 ? 1 : rawThreshold;
    }

    private bool TryBuildDeterministicRecoveryDecision(
        string failedToolName,
        IReadOnlyList<string> missingRequired,
        AgentDecision currentDecision,
        string task,
        string workspaceRoot,
        IReadOnlyList<string> pathHints,
        out AgentDecision recovered,
        out string note)
    {
        recovered = currentDecision;
        note = string.Empty;
        if (missingRequired.Count == 0)
        {
            return false;
        }

        var missingSet = new HashSet<string>(missingRequired, StringComparer.OrdinalIgnoreCase);

        if ((failedToolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
             failedToolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
            missingSet.Contains("query"))
        {
            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["query"] = BuildSeedSearchQuery(task)
            };
            if (failedToolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase) &&
                !ToolArgumentReader.HasValue(currentDecision.Arguments, "task"))
            {
                updates["task"] = task;
            }

            recovered = currentDecision with { Arguments = MergeArguments(currentDecision.Arguments, updates) };
            note = $"Auto-filled missing query for '{failedToolName}' using deterministic task seed.";
            return true;
        }

        if (failedToolName.Equals("exec_shell", StringComparison.OrdinalIgnoreCase) &&
            missingSet.Contains("command") &&
            TryExtractCommandFromRawOutput(task, out var inferredCommand))
        {
            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = inferredCommand
            };
            recovered = currentDecision with { Arguments = MergeArguments(currentDecision.Arguments, updates) };
            note = "Auto-filled missing shell command from task text.";
            return true;
        }

        if (failedToolName.Equals("git_commit", StringComparison.OrdinalIgnoreCase) &&
            missingSet.Contains("message"))
        {
            if (!TryExtractCommitMessage(task, task, out var commitMessage))
            {
                commitMessage = "chore: apply requested changes";
            }

            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["message"] = commitMessage
            };
            recovered = currentDecision with { Arguments = MergeArguments(currentDecision.Arguments, updates) };
            note = "Auto-filled missing commit message.";
            return true;
        }

        if ((failedToolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
             failedToolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase)) &&
            missingSet.Contains("content") &&
            _tools.ContainsKey("fs_read"))
        {
            var path = ToolArgumentReader.GetString(currentDecision.Arguments, "path");
            if (TryNormalizePathCandidate(workspaceRoot, path, allowNonExisting: false, preferFile: true, out var normalizedPath))
            {
                recovered = CreateToolDecision(
                    "fs_read",
                    "deterministic recovery: read existing file to prepare missing write content",
                    $"{{\"path\":{JsonSerializer.Serialize(normalizedPath)},\"max_bytes\":4096}}");
                note = $"Switched to 'fs_read' to recover missing content for '{failedToolName}'.";
                return true;
            }
        }

        if (missingSet.Contains("path"))
        {
            var allowNonExistingPath = failedToolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                                       failedToolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
            var preferFile = !failedToolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase);
            if (TryInferPathFromContext(task, currentDecision.Reason, workspaceRoot, pathHints, allowNonExistingPath, preferFile, out var inferredPath))
            {
                var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = inferredPath
                };
                recovered = currentDecision with { Arguments = MergeArguments(currentDecision.Arguments, updates) };
                note = $"Recovered missing path for '{failedToolName}' from workspace/task hints.";
                return true;
            }

            if (_tools.ContainsKey("fs_list") && !failedToolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase))
            {
                recovered = CreateToolDecision(
                    "fs_list",
                    $"deterministic recovery: collect valid paths because '{failedToolName}' was missing path",
                    "{\"path\":\".\",\"recurse\":false,\"include_hidden\":false}");
                note = $"Switched to 'fs_list' because '{failedToolName}' was missing path.";
                return true;
            }

            if (_tools.ContainsKey("search_lexical") && !failedToolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase))
            {
                var seedQuery = BuildSeedSearchQuery(task);
                recovered = CreateToolDecision(
                    "search_lexical",
                    $"deterministic recovery: locate candidate files before retrying '{failedToolName}'",
                    $"{{\"query\":{JsonSerializer.Serialize(seedQuery)},\"max_results\":12}}");
                note = $"Switched to 'search_lexical' because '{failedToolName}' was missing path.";
                return true;
            }
        }

        return false;
    }

    private AgentDecision? TryCreateBootstrapDecision(
        bool requiresToolBeforeFinal,
        int toolStepsExecuted,
        int consecutiveInvalidResponses,
        string task)
    {
        if (!requiresToolBeforeFinal || toolStepsExecuted > 0 || consecutiveInvalidResponses < 1)
        {
            return null;
        }

        if (_tools.ContainsKey("fs_list"))
        {
            return CreateToolDecision(
                "fs_list",
                "deterministic bootstrap: inspect workspace root before further decisions",
                "{\"path\":\".\",\"recurse\":false,\"include_hidden\":false}");
        }

        if (_tools.ContainsKey("git_status"))
        {
            return CreateToolDecision(
                "git_status",
                "deterministic bootstrap: inspect repository state before further decisions",
                "{}");
        }

        if (_tools.ContainsKey("search_lexical"))
        {
            var seedQuery = BuildSeedSearchQuery(task);
            return CreateToolDecision(
                "search_lexical",
                "deterministic bootstrap: locate candidate code before further decisions",
                $"{{\"query\":{JsonSerializer.Serialize(seedQuery)},\"max_results\":12}}");
        }

        return null;
    }

    private static string BuildSeedSearchQuery(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "TODO";
        }

        var words = task
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= 4 && char.IsLetterOrDigit(w[0]))
            .Take(3)
            .ToArray();

        return words.Length == 0 ? "TODO" : string.Join(' ', words);
    }

    private static AgentDecision CreateToolDecision(string toolName, string reason, string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new AgentDecision(
            AgentDecisionType.Tool,
            toolName,
            doc.RootElement.Clone(),
            reason,
            string.Empty);
    }
}
