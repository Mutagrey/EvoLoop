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
    private readonly ReActDeterministicRecovery _deterministicRecovery;
    private readonly ReActProfileSelection _profileSelection;

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
        _deterministicRecovery = new ReActDeterministicRecovery(_tools);
        _profileSelection = new ReActProfileSelection(config, _modelRouter, _contextFactory);
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

        var profilePlan = _profileSelection.BuildProfilePlan(request.ProfileName);
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
        var toolCallingMode = _profileSelection.ResolveToolCallingMode(currentProfileName);
        var modelAdapter = _modelAdapterRouter.GetAdapter(currentProfileName, toolCallingMode);

        var history = (await _contextBuilder.BuildInitialMessagesAsync(request, context, _memoryStore, ct)).ToList();
        var internalHistory = history.Select(ToInternalMessage).ToList();

        var requiresToolBeforeFinal = TaskLikelyRequiresTools(request.Task);
        var toolStepsExecuted = 0;
        var consecutiveInvalidResponses = 0;
        var consecutiveFinalWithoutTools = 0;
        var lastModelIssue = string.Empty;
        var contextCompactions = 0;
        var pathHints = new ReActPathHints(request.WorkspaceRoot);
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

                    toolCallingMode = _profileSelection.ResolveToolCallingMode(currentProfileName);
                    modelAdapter = _modelAdapterRouter.GetAdapter(currentProfileName, toolCallingMode);
                    var modelRequest = CreateModelTurnRequest(
                        currentProfileName,
                        modelName,
                        systemPrompt,
                        history,
                        internalHistory,
                        toolCallingMode,
                        session.SessionId,
                        step);

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

                    var bootstrapDecision = _deterministicRecovery.TryCreateBootstrapDecision(
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
                        if (consecutiveInvalidResponses >= ReActProfileSelection.GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                            _profileSelection.TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                        {
                            consecutiveInvalidResponses = 0;
                            consecutiveFinalWithoutTools = 0;
                            await observer.OnEventAsync(new AgentRunEvent(
                                AgentRunEventType.ModelProfileSwitched,
                                $"Switched model profile to '{currentProfileName}' after repeated invalid responses.",
                                step), ct);
                            AppendUserHistory(history, internalHistory, $"OBSERVATION: Profile switched to '{currentProfileName}'. Continue with strict JSON tool decisions.");
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
                        AppendUserHistory(
                            history,
                            internalHistory,
                            "OBSERVATION: Response format invalid. Return EXACTLY one JSON object using one of schemas: " +
                            "{\"type\":\"tool\",\"tool\":\"...\",\"reason\":\"...\",\"arguments\":{...}} " +
                            "or {\"type\":\"final\",\"message\":\"...\"} or {\"type\":\"clarify\",\"message\":\"...\"}.");
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

                        var deterministicBootstrap = _deterministicRecovery.TryCreateBootstrapDecision(
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
                            if (consecutiveFinalWithoutTools >= ReActProfileSelection.GetSwitchThreshold(_config.Runtime.FinalWithoutToolsBeforeProfileSwitch) &&
                                _profileSelection.TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                            {
                                consecutiveInvalidResponses = 0;
                                consecutiveFinalWithoutTools = 0;
                                await observer.OnEventAsync(new AgentRunEvent(
                                    AgentRunEventType.ModelProfileSwitched,
                                    $"Switched model profile to '{currentProfileName}' after repeated final-without-tools replies.",
                                    step), ct);
                                AppendUserHistory(history, internalHistory, $"OBSERVATION: Profile switched to '{currentProfileName}'. You must use tools for this task.");
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
                            AppendUserHistory(
                                history,
                                internalHistory,
                                "OBSERVATION: This task requires workspace actions. Call an appropriate tool before returning final.");
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

                    if (consecutiveInvalidResponses >= ReActProfileSelection.GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                        _profileSelection.TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                    {
                        consecutiveInvalidResponses = 0;
                        consecutiveFinalWithoutTools = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelProfileSwitched,
                            $"Switched model profile to '{currentProfileName}' after repeated unknown tool decisions.",
                            step), ct);
                        AppendUserHistory(history, internalHistory, $"OBSERVATION: Profile switched to '{currentProfileName}'. Use only allowed tools: {string.Join(", ", _tools.Keys.OrderBy(x => x))}.");
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
                    AppendUserHistory(history, internalHistory, $"OBSERVATION: Unknown tool '{invalidTool}'. Use one of: {string.Join(", ", _tools.Keys.OrderBy(x => x))}.");
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
                    _deterministicRecovery.TryBuildRecoveryDecision(
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

                    if (consecutiveInvalidResponses >= ReActProfileSelection.GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                        _profileSelection.TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelName, request.WorkspaceRoot, session.SessionId, request.ExecutionMode, request.ApprovalMode, ref context))
                    {
                        consecutiveInvalidResponses = 0;
                        consecutiveFinalWithoutTools = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelProfileSwitched,
                            $"Switched model profile to '{currentProfileName}' after repeated argument-validation failures.",
                            step), ct);
                        AppendUserHistory(history, internalHistory, $"OBSERVATION: Profile switched to '{currentProfileName}'. Tool '{tool.Name}' validation failed: {missing}.");
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
                    AppendUserHistory(
                        history,
                        internalHistory,
                        $"OBSERVATION: Tool '{tool.Name}' call is invalid. Validation errors: {missing}. " +
                        $"Return STRICT JSON tool call and include required fields: {requiredHint}.");
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

                pathHints.Capture(tool.Name, decision.Arguments, result);
                trace.Add(turnResult.Step!);
                toolStepsExecuted++;
                lastModelIssue = result.Success ? string.Empty : $"tool_failed:{tool.Name}";
                var observation = BuildObservationMessage(tool.Name, result, _config.Runtime.ObservationMaxChars);
                var toolResultMessage = CreateToolResultMessage(decision, result, _config.Runtime.ObservationMaxChars);
                AppendToolResultHistory(
                    history,
                    internalHistory,
                    decision,
                    toolResultMessage,
                    modelResult.ToolCallingMode,
                    observation,
                    _config.Runtime.ObservationMaxChars);
            }

            if (!success && finalMessage == "Agent ended without final answer.")
            {
                finalMessage = $"Reached max steps ({maxSteps}) without final answer.";
            }

            await CompleteRunLifecycleAsync(request, observer, session, context, trace, success, finalMessage, ct);

            return new AgentRunResult(success, finalMessage, trace.Count, session.SessionId, trace);
        }
        catch (Exception ex)
        {
            return await CompleteErrorLifecycleAsync(request, observer, session, context, trace, ex, ct);
        }
    }

}
