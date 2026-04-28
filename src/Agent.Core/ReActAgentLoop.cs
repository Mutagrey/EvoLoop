using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core;

public sealed class ReActAgentLoop : IAgentLoop
{
    private readonly IModelClientRouter _modelRouter;
    private readonly IPolicyEngine _policyEngine;
    private readonly IApprovalService _approvalService;
    private readonly IEventStore _eventStore;
    private readonly IWorkspaceMemoryStore _memoryStore;
    private readonly IToolContextFactory _contextFactory;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly AgentConfig _config;

    public ReActAgentLoop(
        IModelClientRouter modelRouter,
        IEnumerable<ITool> tools,
        IPolicyEngine policyEngine,
        IApprovalService approvalService,
        IEventStore eventStore,
        IToolContextFactory contextFactory,
        AgentConfig config,
        IWorkspaceMemoryStore? memoryStore = null)
    {
        _modelRouter = modelRouter;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _policyEngine = policyEngine;
        _approvalService = approvalService;
        _eventStore = eventStore;
        _memoryStore = memoryStore ?? NullWorkspaceMemoryStore.Instance;
        _contextFactory = contextFactory;
        _config = config;
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
        var context = _contextFactory.Create(request.WorkspaceRoot, session.SessionId, currentProfileName);
        var modelClient = _modelRouter.GetClient(currentProfileName);
        var modelName = _modelRouter.ResolveModelName(currentProfileName);

        var history = new List<ModelMessage>();
        history.Add(new ModelMessage("user", BuildRuntimeContextMessage(context.Capabilities)));

        if (_config.Runtime.MemoryEnabled)
        {
            var memoryContext = await _memoryStore.LoadContextAsync(request.WorkspaceRoot, request.Task, ct);
            if (!string.IsNullOrWhiteSpace(memoryContext.Content))
            {
                history.Add(new ModelMessage("user", memoryContext.Content));
                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.MemoryLoaded,
                    $"Loaded workspace memory context ({memoryContext.EntriesUsed} run snippets)."), ct);
            }
        }

        history.Add(new ModelMessage("user", $"TASK:\n{request.Task}"));

        var requiresToolBeforeFinal = TaskLikelyRequiresTools(request.Task);
        var toolStepsExecuted = 0;
        var consecutiveInvalidResponses = 0;
        var consecutiveFinalWithoutTools = 0;
        var lastModelIssue = string.Empty;
        var contextCompactions = 0;
        var pathHints = new List<string>();

        string finalMessage = "Agent ended without final answer.";
        bool success = false;

        try
        {
            for (var step = 1; step <= maxSteps; step++)
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
                var systemPrompt = BuildSystemPrompt(_tools.Values);
                if (!string.IsNullOrWhiteSpace(adaptiveDirective))
                {
                    systemPrompt += "\n\nADAPTIVE EXECUTION DIRECTIVE:\n" + adaptiveDirective;
                }

                var modelRequest = new ModelTurnRequest(
                    currentProfileName,
                    modelName,
                    systemPrompt,
                    history,
                    GetTemperature(currentProfileName),
                    GetMaxTokens(currentProfileName),
                    new Dictionary<string, string>
                    {
                        ["session_id"] = session.SessionId,
                        ["step"] = step.ToString(),
                        ["profile"] = currentProfileName
                    });

                var modelResult = await modelClient.CompleteAsync(modelRequest, ct);

                await observer.OnEventAsync(new AgentRunEvent(AgentRunEventType.ModelCallCompleted, "Model response received", step), ct);

                var decision = AgentDecisionParser.Parse(modelResult.Content, _tools.Keys);
                if (decision.Type == AgentDecisionType.Invalid)
                {
                    if (!requiresToolBeforeFinal && TryRecoverPlainFinalMessage(modelResult.Content, out var recoveredFinal))
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
                            TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelClient, ref modelName, request.WorkspaceRoot, session.SessionId, ref context))
                        {
                            consecutiveInvalidResponses = 0;
                            consecutiveFinalWithoutTools = 0;
                            await observer.OnEventAsync(new AgentRunEvent(
                                AgentRunEventType.ModelProfileSwitched,
                                $"Switched model profile to '{currentProfileName}' after repeated invalid responses.",
                                step), ct);
                            history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. Continue with strict JSON tool decisions."));
                            continue;
                        }

                        if (consecutiveInvalidResponses >= _config.Runtime.MaxInvalidModelResponses)
                        {
                            finalMessage = $"Stopped after {consecutiveInvalidResponses} invalid model responses. Model must return strict JSON tool/final schema.";
                            success = false;
                            break;
                        }

                        history.Add(new ModelMessage("assistant", modelResult.Content));
                        history.Add(new ModelMessage(
                            "user",
                            "OBSERVATION: Response format invalid. Return EXACTLY one JSON object using one of schemas: " +
                            "{\"type\":\"tool\",\"tool\":\"...\",\"reason\":\"...\",\"arguments\":{...}} " +
                            "or {\"type\":\"final\",\"message\":\"...\"} or {\"type\":\"clarify\",\"message\":\"...\"}."));
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
                                TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelClient, ref modelName, request.WorkspaceRoot, session.SessionId, ref context))
                            {
                                consecutiveInvalidResponses = 0;
                                consecutiveFinalWithoutTools = 0;
                                await observer.OnEventAsync(new AgentRunEvent(
                                    AgentRunEventType.ModelProfileSwitched,
                                    $"Switched model profile to '{currentProfileName}' after repeated final-without-tools replies.",
                                    step), ct);
                                history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. You must use tools for this task."));
                                continue;
                            }

                            if (consecutiveFinalWithoutTools >= _config.Runtime.MaxConsecutiveFinalWithoutTools)
                            {
                                finalMessage = $"Stopped after {consecutiveFinalWithoutTools} final-only replies without any tool calls for an action-oriented task.";
                                success = false;
                                break;
                            }

                            history.Add(new ModelMessage("assistant", modelResult.Content));
                            history.Add(new ModelMessage(
                                "user",
                                "OBSERVATION: This task requires workspace actions. Call an appropriate tool before returning final."));
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
                        TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelClient, ref modelName, request.WorkspaceRoot, session.SessionId, ref context))
                    {
                        consecutiveInvalidResponses = 0;
                        consecutiveFinalWithoutTools = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelProfileSwitched,
                            $"Switched model profile to '{currentProfileName}' after repeated unknown tool decisions.",
                            step), ct);
                        history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. Use only allowed tools: {string.Join(", ", _tools.Keys.OrderBy(x => x))}."));
                        continue;
                    }

                    if (consecutiveInvalidResponses >= _config.Runtime.MaxInvalidModelResponses)
                    {
                        finalMessage = $"Stopped after {consecutiveInvalidResponses} invalid model decisions (format/unknown tool).";
                        success = false;
                        break;
                    }

                    history.Add(new ModelMessage("assistant", modelResult.Content));
                    history.Add(new ModelMessage("user", $"OBSERVATION: Unknown tool '{invalidTool}'. Use one of: {string.Join(", ", _tools.Keys.OrderBy(x => x))}."));
                    continue;
                }

                if (TryRepairToolDecision(
                    tool.Name,
                    decision,
                    request.Task,
                    request.WorkspaceRoot,
                    pathHints,
                    modelResult.Content,
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

                var missingRequired = tool.Schema.RequiredFields
                    .Where(required => !ToolArgumentReader.HasValue(decision.Arguments, required))
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
                    missingRequired = tool.Schema.RequiredFields
                        .Where(required => !ToolArgumentReader.HasValue(decision.Arguments, required))
                        .ToArray();
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelDecisionRecovered,
                        recoveryNote,
                        step,
                        tool.Name), ct);
                }

                if (missingRequired.Length > 0)
                {
                    consecutiveInvalidResponses++;
                    lastModelIssue = $"missing_required_arguments:{tool.Name}";
                    var missing = string.Join(", ", missingRequired);
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ModelResponseInvalid,
                        $"Tool '{tool.Name}' missing required arguments: {missing}",
                        step,
                        tool.Name), ct);

                    if (consecutiveInvalidResponses >= GetSwitchThreshold(_config.Runtime.InvalidResponsesBeforeProfileSwitch) &&
                        TrySwitchProfile(profilePlan, ref profileIndex, ref currentProfileName, ref modelClient, ref modelName, request.WorkspaceRoot, session.SessionId, ref context))
                    {
                        consecutiveInvalidResponses = 0;
                        consecutiveFinalWithoutTools = 0;
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ModelProfileSwitched,
                            $"Switched model profile to '{currentProfileName}' after repeated argument-validation failures.",
                            step), ct);
                        history.Add(new ModelMessage("user", $"OBSERVATION: Profile switched to '{currentProfileName}'. Tool '{tool.Name}' requires: {missing}."));
                        continue;
                    }

                    if (consecutiveInvalidResponses >= _config.Runtime.MaxInvalidModelResponses)
                    {
                        finalMessage = $"Stopped after {consecutiveInvalidResponses} invalid model decisions (format/unknown tool/missing arguments).";
                        success = false;
                        break;
                    }

                    var requiredHint = string.Join(", ", tool.Schema.RequiredFields);
                    history.Add(new ModelMessage("assistant", modelResult.Content));
                    history.Add(new ModelMessage(
                        "user",
                        $"OBSERVATION: Tool '{tool.Name}' call is invalid. Missing required arguments: {missing}. " +
                        $"Return STRICT JSON tool call and include required fields: {requiredHint}."));
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
                var policyDecision = _policyEngine.Evaluate(call, context);

                if (policyDecision.Kind == PolicyDecisionKind.Deny)
                {
                    lastModelIssue = "policy_denied";
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.PolicyDenied,
                        policyDecision.Reason,
                        step,
                        tool.Name), ct);

                    history.Add(new ModelMessage("assistant", modelResult.Content));
                    history.Add(new ModelMessage("user", $"OBSERVATION: Policy denied this action: {policyDecision.Reason}"));

                    var deniedStep = new SessionStep(
                        session.SessionId,
                        step,
                        "tool",
                        tool.Name,
                        decision.Reason,
                        false,
                        "Policy denied",
                        DateTimeOffset.UtcNow,
                        0,
                        policyDecision.Reason);

                    trace.Add(deniedStep);
                    await _eventStore.AppendStepAsync(deniedStep, ct);
                    continue;
                }

                if (policyDecision.Kind == PolicyDecisionKind.RequireApproval)
                {
                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ApprovalRequired,
                        policyDecision.Reason,
                        step,
                        tool.Name), ct);

                    var approved = await _approvalService.RequestApprovalAsync(new ApprovalRequest(
                        tool.Name,
                        policyDecision.Reason,
                        PreviewArguments(decision.Arguments)), ct);

                    if (!approved)
                    {
                        lastModelIssue = "approval_rejected";
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.ApprovalRejected,
                            "User rejected action",
                            step,
                            tool.Name), ct);

                        history.Add(new ModelMessage("assistant", modelResult.Content));
                        history.Add(new ModelMessage("user", "OBSERVATION: User rejected this action. Choose a safer alternative."));

                        var rejectedStep = new SessionStep(
                            session.SessionId,
                            step,
                            "tool",
                            tool.Name,
                            decision.Reason,
                            false,
                            "User rejected",
                            DateTimeOffset.UtcNow,
                            0,
                            "Approval rejected");

                        trace.Add(rejectedStep);
                        await _eventStore.AppendStepAsync(rejectedStep, ct);
                        continue;
                    }

                    await observer.OnEventAsync(new AgentRunEvent(
                        AgentRunEventType.ApprovalGranted,
                        "User approved action",
                        step,
                        tool.Name), ct);
                }

                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ToolExecutionStarted,
                    BuildToolRunMessage(tool.Name, decision.Arguments),
                    step,
                    tool.Name), ct);

                var stopwatch = Stopwatch.StartNew();
                ToolResult result;

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.Runtime.ToolTimeoutSeconds));
                    result = await tool.ExecuteAsync(call, context, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = new ToolResult(false, "Tool timed out.", null, null);
                }
                catch (Exception ex)
                {
                    result = new ToolResult(false, $"Tool execution threw exception: {ex.Message}", null, ex.ToString());
                }

                stopwatch.Stop();

                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ToolExecutionCompleted,
                    BuildToolCompletionMessage(tool.Name, decision.Arguments, result),
                    step,
                    tool.Name), ct);

                CapturePathHints(pathHints, request.WorkspaceRoot, tool.Name, decision.Arguments, result);

                var primaryOutput = !string.IsNullOrWhiteSpace(result.StdOut)
                    ? result.StdOut
                    : result.Message;
                var output = TruncateOutput(primaryOutput, _config.Runtime.MaxOutputBytes);
                var stepRecord = new SessionStep(
                    session.SessionId,
                    step,
                    "tool",
                    tool.Name,
                    decision.Reason,
                    result.Success,
                    output,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    result.Success ? null : result.StdErr ?? result.Message);

                trace.Add(stepRecord);
                await _eventStore.AppendStepAsync(stepRecord, ct);
                toolStepsExecuted++;
                lastModelIssue = result.Success ? string.Empty : $"tool_failed:{tool.Name}";

                history.Add(new ModelMessage("assistant", modelResult.Content));
                history.Add(new ModelMessage("user", BuildObservationMessage(tool.Name, result, _config.Runtime.ObservationMaxChars)));
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

            return new AgentRunResult(success, finalMessage, trace.Count, session.SessionId, trace);
        }
        catch (Exception ex)
        {
            await _eventStore.CompleteSessionAsync(session.SessionId, "error", ct);

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

    private static string BuildSystemPrompt(IEnumerable<ITool> tools)
    {
        var toolList = tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var hasFsList = toolList.Any(t => t.Name.Equals("fs_list", StringComparison.OrdinalIgnoreCase));
        var hasGitStatus = toolList.Any(t => t.Name.Equals("git_status", StringComparison.OrdinalIgnoreCase));
        var hasSearchLexical = toolList.Any(t => t.Name.Equals("search_lexical", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.AppendLine("You are EvoLoop Agent, a professional autonomous coding CLI engineer.");
        sb.AppendLine("Primary objective: complete the user's task by using tools and producing concrete workspace outcomes.");
        sb.AppendLine("Execution model: ReAct loop (analyze -> one tool call -> observe -> repeat).");
        sb.AppendLine("Output contract: return STRICT JSON only. No markdown, prose wrappers, or code fences.");
        sb.AppendLine("The first non-whitespace character of your response MUST be '{' and the last MUST be '}'.");
        sb.AppendLine("You are not a chat assistant in this step. You are a machine that emits only one JSON object.");
        sb.AppendLine("Decision schema:");
        sb.AppendLine("For tool call: {\"type\":\"tool\",\"tool\":\"tool_name\",\"reason\":\"why\",\"arguments\":{...}}");
        sb.AppendLine("For final response: {\"type\":\"final\",\"message\":\"...\"}");
        sb.AppendLine("For clarification request: {\"type\":\"clarify\",\"message\":\"...\"}");
        sb.AppendLine("Formatting constraints:");
        sb.AppendLine("- Exactly one JSON object.");
        sb.AppendLine("- No extra keys outside schema unless needed for tool arguments.");
        sb.AppendLine("- No comments. No trailing commas.");
        sb.AppendLine("- Do not wrap JSON in markdown.");
        sb.AppendLine("- For tool calls, include all required fields exactly as listed for that tool.");
        sb.AppendLine("- If any required field is unknown (especially path/content), first call a discovery tool (fs_list/search_lexical/fs_read).");
        sb.AppendLine("Available tools:");

        foreach (var tool in toolList)
        {
            sb.AppendLine($"- {tool.Name}: {tool.Schema.Description}");
            if (tool.Schema.RequiredFields.Count > 0)
            {
                sb.AppendLine($"  required: {string.Join(", ", tool.Schema.RequiredFields)}");
            }
        }

        sb.AppendLine("Rules:");
        sb.AppendLine("- The environment may be Windows-first, offline or restricted, and may not allow admin rights or dependency installation.");
        sb.AppendLine("- Prefer repo-contained, self-contained, low-dependency changes.");
        sb.AppendLine("- Do not assume package installation, internet downloads, or privileged setup steps are available.");
        sb.AppendLine("- If runtime capabilities indicate a tool or dependency is unavailable, adapt to the available fallbacks.");
        sb.AppendLine("- You may receive WORKSPACE MEMORY context from previous runs; use it as hint, verify with tools before acting.");
        sb.AppendLine("- Use only listed tools.");
        sb.AppendLine("- Read before you write.");
        sb.AppendLine("- Keep steps minimal and deterministic.");
        sb.AppendLine("- Prefer direct file edits and concrete command execution over abstract advice.");
        sb.AppendLine("- For action-oriented tasks (create/edit/delete/run/git), call a tool instead of responding with explanation.");
        sb.AppendLine("- Do not return final until you have enough tool observations to justify completion.");
        sb.AppendLine("- If the task asks to inspect/change files, run commands, or use git, you MUST call tools before final.");
        sb.AppendLine("- Do not claim actions unless tool observations confirm them.");
        sb.AppendLine("- If task is done, return final.");
        sb.AppendLine("Good decision examples:");
        if (hasFsList)
        {
            sb.AppendLine("{\"type\":\"tool\",\"tool\":\"fs_list\",\"reason\":\"inspect workspace root before edits\",\"arguments\":{\"path\":\".\",\"recurse\":false,\"include_hidden\":false}}");
        }
        if (hasGitStatus)
        {
            sb.AppendLine("{\"type\":\"tool\",\"tool\":\"git_status\",\"reason\":\"check repository state before making changes\",\"arguments\":{}}");
        }
        if (hasSearchLexical)
        {
            sb.AppendLine("{\"type\":\"tool\",\"tool\":\"search_lexical\",\"reason\":\"locate relevant code before editing\",\"arguments\":{\"query\":\"target symbol\",\"max_results\":20}}");
        }
        sb.AppendLine("{\"type\":\"final\",\"message\":\"Completed requested changes and verified with tool outputs.\"}");
        return sb.ToString();
    }

    private static string BuildRuntimeContextMessage(RuntimeCapabilities capabilities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RUNTIME ENVIRONMENT (verify decisions against these constraints):");
        sb.AppendLine($"- operating_mode: {capabilities.ModeLabel}");
        sb.AppendLine($"- platform: {capabilities.Platform}");
        sb.AppendLine($"- shell_available: {capabilities.ShellAvailable}");
        sb.AppendLine($"- workspace_writable: {capabilities.WorkspaceWritable}");
        sb.AppendLine($"- git_available: {capabilities.GitAvailable}");
        sb.AppendLine($"- rg_available: {capabilities.RipgrepAvailable}");
        sb.AppendLine($"- sqlite_available: {capabilities.SqliteAvailable}");
        sb.AppendLine($"- model_configured: {capabilities.ModelConfigured}");
        sb.AppendLine($"- model_reachable: {capabilities.ModelReachable}");
        sb.AppendLine("Constraints:");
        sb.AppendLine("- target workflows should not require admin rights");
        sb.AppendLine("- avoid proposing dependency installs unless explicitly requested");
        sb.AppendLine("- prefer self-contained or built-in solutions");
        sb.AppendLine("- if a capability is unavailable, use fallbacks instead of assuming it exists");
        return sb.ToString().TrimEnd();
    }

    private static string BuildObservationMessage(string toolName, ToolResult result, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OBSERVATION from {toolName}");
        sb.AppendLine($"success: {result.Success}");
        sb.AppendLine($"message: {ToOneLine(result.Message, 400)}");

        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine("stdout:");
            sb.AppendLine(ClipToChars(result.StdOut, Math.Max(200, maxChars / 2)));
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            sb.AppendLine("stderr:");
            sb.AppendLine(ClipToChars(result.StdErr, Math.Max(120, maxChars / 3)));
        }

        return ClipToChars(sb.ToString(), Math.Max(300, maxChars));
    }

    private static string BuildToolPlanMessage(string toolName, string reason)
    {
        var headline = toolName switch
        {
            "fs_list" => "Inspect workspace structure",
            "fs_read" => "Read target file",
            "fs_write" => "Write/update file",
            "fs_patch" => "Apply patch to file",
            "fs_delete" => "Delete file or directory",
            "search_lexical" => "Find matching code by text search",
            "search_semantic" => "Find relevant code by reranked search",
            "exec_shell" => "Run shell command",
            "git_status" => "Check git status",
            "git_diff" => "Inspect git diff",
            "git_log" => "Inspect recent commits",
            "git_show" => "Inspect commit/object",
            "git_add" => "Stage changes",
            "git_commit" => "Create commit",
            _ => $"Use tool {toolName}"
        };

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : ToOneLine(reason, 120);
        var reasonProbe = normalizedReason.TrimStart();
        if (reasonProbe.StartsWith("{", StringComparison.Ordinal) ||
            reasonProbe.StartsWith("[", StringComparison.Ordinal) ||
            reasonProbe.StartsWith("```", StringComparison.Ordinal))
        {
            normalizedReason = string.Empty;
        }

        return string.IsNullOrWhiteSpace(normalizedReason)
            ? headline
            : $"{headline}. {normalizedReason}";
    }

    private static string BuildToolRunMessage(string toolName, JsonElement arguments)
    {
        var path = ToolArgumentReader.GetString(arguments, "path");
        var query = ToolArgumentReader.GetString(arguments, "query");
        var command = ToolArgumentReader.GetString(arguments, "command");
        var gitRef = ToolArgumentReader.GetString(arguments, "ref");
        var queryText = string.IsNullOrWhiteSpace(query) ? "<query>" : ToOneLine(query, 80);
        var commandText = string.IsNullOrWhiteSpace(command) ? "<command>" : ToOneLine(command, 120);

        return toolName switch
        {
            "fs_list" => $"Exploring directory {path ?? "."}",
            "fs_read" => $"Exploring file {path ?? "<missing path>"}",
            "fs_write" => $"Editing file {path ?? "<missing path>"}",
            "fs_patch" => $"Editing file {path ?? "<missing path>"}",
            "fs_delete" => $"Deleting path {path ?? "<missing path>"}",
            "search_lexical" => $"Exploring search query \"{queryText}\"",
            "search_semantic" => $"Exploring semantic query \"{queryText}\"",
            "exec_shell" => $"Running command: {commandText}",
            "git_status" => "Running git status --short --branch",
            "git_diff" => "Running git diff",
            "git_log" => "Running git log --oneline",
            "git_show" => $"Running git show --stat {gitRef ?? "HEAD"}",
            "git_add" => $"Running git add -- {ToolArgumentReader.GetString(arguments, "pathspec") ?? "."}",
            "git_commit" => "Running git commit -m <message>",
            _ => $"Running tool {toolName}"
        };
    }

    private static string BuildToolCompletionMessage(string toolName, JsonElement arguments, ToolResult result)
    {
        if (!result.Success)
        {
            var failReason = !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr : result.Message;
            return $"{toolName} failed: {ToOneLine(failReason, 180)}";
        }

        var path = ToolArgumentReader.GetString(arguments, "path");
        var query = ToolArgumentReader.GetString(arguments, "query");
        var command = ToolArgumentReader.GetString(arguments, "command");
        var gitRef = ToolArgumentReader.GetString(arguments, "ref");
        var queryText = string.IsNullOrWhiteSpace(query) ? "<query>" : ToOneLine(query, 80);
        var commandText = string.IsNullOrWhiteSpace(command) ? "<command>" : ToOneLine(command, 140);

        return toolName switch
        {
            "fs_list" => $"Explored {(path ?? ".")}",
            "fs_read" => $"Explored {path ?? "<missing path>"}",
            "fs_write" => $"Edited {path ?? "<missing path>"}",
            "fs_patch" => $"Edited {path ?? "<missing path>"}",
            "fs_delete" => $"Deleted {path ?? "<missing path>"}",
            "search_lexical" => $"Searched \"{queryText}\"",
            "search_semantic" => $"Searched semantically \"{queryText}\"",
            "exec_shell" => $"Ran {commandText}",
            "git_status" => "Ran git status --short --branch",
            "git_diff" => "Ran git diff",
            "git_log" => "Ran git log --oneline",
            "git_show" => $"Ran git show --stat {gitRef ?? "HEAD"}",
            "git_add" => $"Ran git add -- {ToolArgumentReader.GetString(arguments, "pathspec") ?? "."}",
            "git_commit" => "Ran git commit -m <message>",
            _ => $"{toolName} completed"
        };
    }

    private static string ToOneLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= maxLength)
        {
            return oneLine;
        }

        return oneLine[..maxLength] + "...";
    }

    private static string PreviewArguments(JsonElement args)
    {
        var raw = args.GetRawText();
        return raw.Length <= 300 ? raw : raw[..300] + "...";
    }

    private static string TruncateOutput(string input, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(input);
        if (bytes <= maxBytes)
        {
            return input;
        }

        var truncated = input;
        while (Encoding.UTF8.GetByteCount(truncated) > maxBytes && truncated.Length > 0)
        {
            truncated = truncated[..Math.Max(0, truncated.Length - 128)];
        }

        return truncated + "\n[truncated]";
    }

    private static string ClipToChars(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        if (maxChars <= 3)
        {
            return value[..Math.Max(0, maxChars)];
        }

        return value[..(maxChars - 3)] + "...";
    }

    private static string BuildAdaptiveDirective(
        bool requiresToolBeforeFinal,
        int toolStepsExecuted,
        int consecutiveInvalidResponses,
        int consecutiveFinalWithoutTools,
        string lastModelIssue)
    {
        if (consecutiveInvalidResponses <= 0 &&
            consecutiveFinalWithoutTools <= 0 &&
            string.IsNullOrWhiteSpace(lastModelIssue))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Adapt output strategy now.");
        sb.AppendLine("Return exactly one JSON object and nothing else.");
        sb.AppendLine("Allowed schemas:");
        sb.AppendLine("1) {\"type\":\"tool\",\"tool\":\"...\",\"reason\":\"...\",\"arguments\":{...}}");
        sb.AppendLine("2) {\"type\":\"final\",\"message\":\"...\"}");
        sb.AppendLine("3) {\"type\":\"clarify\",\"message\":\"...\"}");

        if (requiresToolBeforeFinal && toolStepsExecuted == 0)
        {
            sb.AppendLine("Do NOT return final yet. Call one appropriate tool first.");
        }

        if (consecutiveInvalidResponses > 0)
        {
            sb.AppendLine($"Recent format failures: {consecutiveInvalidResponses}. Keep JSON compact and valid.");
            sb.AppendLine("Do not include markdown/code fences/comments.");
        }

        if (consecutiveFinalWithoutTools > 0)
        {
            sb.AppendLine($"Recent premature final replies: {consecutiveFinalWithoutTools}. Execute a tool first.");
        }

        if (!string.IsNullOrWhiteSpace(lastModelIssue))
        {
            sb.AppendLine($"Last detected issue: {ToOneLine(lastModelIssue, 120)}.");
            if (lastModelIssue.StartsWith("missing_required_arguments:", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("When calling a tool, every required argument must be present and non-empty.");
                sb.AppendLine("If path is unknown, call fs_list or search_lexical first, then retry with concrete path.");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryCompactHistory(
        List<ModelMessage> history,
        RuntimeConfig runtime,
        out string summary)
    {
        summary = string.Empty;
        var maxMessages = Math.Max(20, runtime.HistoryMaxMessages);
        var maxChars = Math.Max(12000, runtime.HistoryMaxChars);
        if (history.Count <= maxMessages && EstimateHistoryChars(history) <= maxChars)
        {
            return false;
        }

        var keepHead = Math.Min(history.Count, 2);
        var keepTail = Math.Clamp(runtime.HistoryKeepTailMessages, 8, Math.Max(8, maxMessages - keepHead - 1));
        var tailStart = Math.Max(keepHead, history.Count - keepTail);
        if (tailStart <= keepHead)
        {
            return false;
        }

        var middle = history.Skip(keepHead).Take(tailStart - keepHead).ToList();
        if (middle.Count == 0)
        {
            return false;
        }

        var compacted = BuildCompactedHistoryMessage(middle, Math.Max(1200, maxChars / 3));
        var oldCount = history.Count;
        history.RemoveRange(keepHead, tailStart - keepHead);
        history.Insert(keepHead, new ModelMessage("user", compacted));
        summary = $"history messages {oldCount} -> {history.Count}";
        return true;
    }

    private static int EstimateHistoryChars(IEnumerable<ModelMessage> history)
    {
        var total = 0;
        foreach (var message in history)
        {
            total += message.Content?.Length ?? 0;
        }

        return total;
    }

    private static string BuildCompactedHistoryMessage(IReadOnlyList<ModelMessage> middle, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine("COMPACTED CONTEXT SUMMARY (older exchanges):");

        var emitted = 0;
        foreach (var message in middle)
        {
            var content = message.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (content.StartsWith("COMPACTED CONTEXT SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = SummarizeMessageForCompaction(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            sb.Append("- ").AppendLine(normalized);
            emitted++;
            if (emitted >= 20)
            {
                break;
            }
        }

        if (emitted == 0)
        {
            sb.AppendLine("- Previous conversation details were compacted.");
        }

        sb.AppendLine("Use this summary as context. Prefer current tool observations over older details.");
        return ClipToChars(sb.ToString().TrimEnd(), maxChars);
    }

    private static string SummarizeMessageForCompaction(ModelMessage message)
    {
        var content = message.Content ?? string.Empty;
        if (content.StartsWith("OBSERVATION from ", StringComparison.OrdinalIgnoreCase))
        {
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var tool = lines.Length > 0 ? lines[0].Replace("OBSERVATION from ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim() : "tool";
            var success = lines.FirstOrDefault(line => line.StartsWith("success:", StringComparison.OrdinalIgnoreCase)) ?? "success: ?";
            var msg = lines.FirstOrDefault(line => line.StartsWith("message:", StringComparison.OrdinalIgnoreCase)) ?? "message: <none>";
            return $"{tool} | {ToOneLine(success, 48)} | {ToOneLine(msg, 160)}";
        }

        if (content.StartsWith("OBSERVATION:", StringComparison.OrdinalIgnoreCase))
        {
            return ToOneLine(content["OBSERVATION:".Length..].Trim(), 220);
        }

        if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
            content.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return ToOneLine(content, 220);
    }

    private static bool TryRecoverPlainFinalMessage(string content, out string recovered)
    {
        recovered = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        if (trimmed.Length < 12)
        {
            return false;
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("\"type\"") || lower.Contains("```") || lower.Contains("tool") || lower.Contains("action:"))
        {
            return false;
        }

        recovered = ClipToChars(trimmed, 2000);
        return true;
    }

    private static bool TryRepairToolDecision(
        string toolName,
        AgentDecision decision,
        string task,
        string workspaceRoot,
        IReadOnlyList<string> pathHints,
        string rawModelOutput,
        out AgentDecision repaired,
        out string repairNote)
    {
        var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (toolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "path"))
        {
            if (TryExtractPathFromRawOutput(rawModelOutput, workspaceRoot, allowNonExisting: true, preferFile: false, out var listPath))
            {
                updates["path"] = listPath;
            }
            else
            {
                updates["path"] = ".";
            }
        }

        if ((toolName.Equals("fs_read", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase)) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "path"))
        {
            var allowNonExistingPath = toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                                       toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
            var preferFilePath = !toolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase);
            if (TryExtractPathFromRawOutput(rawModelOutput, workspaceRoot, allowNonExistingPath, preferFilePath, out var rawPath))
            {
                updates["path"] = rawPath;
            }
            else if (TryInferPathFromContext(task, decision.Reason, workspaceRoot, pathHints, allowNonExistingPath, preferFilePath, out var inferredPath))
            {
                updates["path"] = inferredPath;
            }
        }

        if (toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "content"))
        {
            if (TryExtractContentFromRawOutput(rawModelOutput, out var content))
            {
                updates["content"] = content;
            }
        }

        if (toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "unified_diff") &&
            !ToolArgumentReader.HasValue(decision.Arguments, "content"))
        {
            if (TryExtractUnifiedDiffFromRawOutput(rawModelOutput, out var diff))
            {
                updates["unified_diff"] = diff;
            }
            else if (TryExtractContentFromRawOutput(rawModelOutput, out var patchContent))
            {
                updates["content"] = patchContent;
            }
        }

        if (toolName.Equals("exec_shell", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "command"))
        {
            if (TryExtractCommandFromRawOutput(rawModelOutput, out var command))
            {
                updates["command"] = command;
            }
        }

        if (toolName.Equals("git_commit", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "message"))
        {
            if (TryExtractCommitMessage(rawModelOutput, task, out var commitMessage))
            {
                updates["message"] = commitMessage;
            }
        }

        if (toolName.Equals("git_show", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "ref"))
        {
            if (TryExtractGitRefFromRawOutput(rawModelOutput, out var gitRef))
            {
                updates["ref"] = gitRef;
            }
            else
            {
                updates["ref"] = "HEAD";
            }
        }

        if (toolName.Equals("git_add", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "pathspec"))
        {
            if (TryExtractPathFromRawOutput(rawModelOutput, workspaceRoot, allowNonExisting: true, preferFile: false, out var pathspec))
            {
                updates["pathspec"] = pathspec;
            }
            else
            {
                updates["pathspec"] = ".";
            }
        }

        if ((toolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "query"))
        {
            if (TryExtractSearchQueryFromRawOutput(rawModelOutput, out var query))
            {
                updates["query"] = query;
            }
            else
            {
                updates["query"] = BuildSeedSearchQuery(task);
            }
        }

        if (toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "task") &&
            !string.IsNullOrWhiteSpace(task))
        {
            updates["task"] = task;
        }

        if (updates.Count == 0)
        {
            repaired = decision;
            repairNote = string.Empty;
            return false;
        }

        var merged = MergeArguments(decision.Arguments, updates);
        repaired = decision with { Arguments = merged };
        repairNote = $"Auto-repaired arguments for '{toolName}': {string.Join(", ", updates.Keys)}.";
        return true;
    }

    private static bool TryExtractPathFromRawOutput(
        string rawModelOutput,
        string workspaceRoot,
        bool allowNonExisting,
        bool preferFile,
        out string path)
    {
        path = string.Empty;

        if (TryExtractNamedScalarValue(rawModelOutput,
            new[] { "path", "file", "file_path", "filepath", "filename", "target_path", "relative_path", "pathspec" },
            out var byKey) &&
            TryNormalizePathCandidate(workspaceRoot, byKey, allowNonExisting, preferFile, out path))
        {
            return true;
        }

        var patchFile = Regex.Match(rawModelOutput, @"(?im)^\*\*\*\s+(?:Add|Update|Delete)\s+File:\s*(?<path>.+)$");
        if (patchFile.Success &&
            TryNormalizePathCandidate(workspaceRoot, patchFile.Groups["path"].Value, allowNonExisting, preferFile, out path))
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(rawModelOutput, @"(?<![\w/\\])(?<path>[A-Za-z0-9_\-./\\]{2,260}\.[A-Za-z0-9_\-]{1,16})(?::\d+)?(?![\w/\\])"))
        {
            var candidate = match.Groups["path"].Value;
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (Match match in Regex.Matches(rawModelOutput, "[`\"'](?<path>[^`\"'\\r\\n]{1,260})[`\"']"))
        {
            var candidate = match.Groups["path"].Value.Trim();
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (Match match in Regex.Matches(rawModelOutput, @"(?<![\w])(?<path>(?:\.{1,2}|[A-Za-z0-9_\-]+)(?:[/\\][A-Za-z0-9_.\-]+)+[/\\]?)(?![\w])"))
        {
            var candidate = match.Groups["path"].Value;
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractSearchQueryFromRawOutput(string rawModelOutput, out string query)
    {
        query = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "query", "search", "pattern", "keyword", "term", "text" }, out var byKey))
        {
            query = ToOneLine(byKey, 180);
            return !string.IsNullOrWhiteSpace(query);
        }

        var quoted = Regex.Match(rawModelOutput, "(?im)\\bsearch(?:\\s+for)?\\s+[\"'`](?<q>[^\"'`\\r\\n]{2,220})[\"'`]");
        if (quoted.Success)
        {
            query = ToOneLine(quoted.Groups["q"].Value, 180);
            return true;
        }

        return false;
    }

    private static bool TryExtractCommandFromRawOutput(string rawModelOutput, out string command)
    {
        command = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "command", "cmd", "shell", "script" }, out var byKey))
        {
            command = ClipToChars(byKey.Trim(), 1000);
            return !string.IsNullOrWhiteSpace(command);
        }

        foreach (var fence in ExtractCodeFences(rawModelOutput))
        {
            if (!IsShellLanguage(fence.Lang) && !LooksLikeCommandBlock(fence.Body))
            {
                continue;
            }

            var normalized = NormalizeShellCommandBlock(fence.Body);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                command = ClipToChars(normalized, 1000);
                return true;
            }
        }

        var promptLine = Regex.Match(rawModelOutput, @"(?im)^\s*\$\s+(?<cmd>.+)$");
        if (promptLine.Success)
        {
            command = ClipToChars(promptLine.Groups["cmd"].Value.Trim(), 1000);
            return true;
        }

        return false;
    }

    private static bool TryExtractCommitMessage(string rawModelOutput, string task, out string message)
    {
        message = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "message", "msg", "commit_message" }, out var byKey))
        {
            message = ClipToChars(byKey.Trim(), 180);
            return !string.IsNullOrWhiteSpace(message);
        }

        var commitQuoted = Regex.Match(rawModelOutput, "(?im)\\bcommit(?:\\s+message)?\\s*[:=]\\s*[\"'`](?<m>[^\"'`\\r\\n]{3,180})[\"'`]");
        if (commitQuoted.Success)
        {
            message = commitQuoted.Groups["m"].Value.Trim();
            return true;
        }

        var taskQuoted = Regex.Match(task ?? string.Empty, "[\"'`](?<m>[^\"'`\\r\\n]{3,180})[\"'`]");
        if (taskQuoted.Success)
        {
            message = taskQuoted.Groups["m"].Value.Trim();
            return true;
        }

        return false;
    }

    private static bool TryExtractGitRefFromRawOutput(string rawModelOutput, out string gitRef)
    {
        gitRef = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "ref", "revision", "sha", "commit" }, out var byKey))
        {
            gitRef = byKey.Trim();
            return !string.IsNullOrWhiteSpace(gitRef);
        }

        var headRef = Regex.Match(rawModelOutput, @"\bHEAD(?:~\d+)?\b", RegexOptions.IgnoreCase);
        if (headRef.Success)
        {
            gitRef = headRef.Value.ToUpperInvariant();
            return true;
        }

        var hash = Regex.Match(rawModelOutput, @"\b[0-9a-fA-F]{7,40}\b");
        if (hash.Success)
        {
            gitRef = hash.Value;
            return true;
        }

        return false;
    }

    private static bool TryExtractContentFromRawOutput(string rawModelOutput, out string content)
    {
        content = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "content", "body", "new_content", "text" }, out var byKey))
        {
            content = ClipToChars(byKey, 32000);
            return !string.IsNullOrWhiteSpace(content);
        }

        foreach (var fence in ExtractCodeFences(rawModelOutput))
        {
            if (IsShellLanguage(fence.Lang) || IsDiffLanguage(fence.Lang))
            {
                continue;
            }

            var candidate = fence.Body.Trim('\r', '\n');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                content = ClipToChars(candidate, 32000);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractUnifiedDiffFromRawOutput(string rawModelOutput, out string unifiedDiff)
    {
        unifiedDiff = string.Empty;
        foreach (var fence in ExtractCodeFences(rawModelOutput))
        {
            if (!IsDiffLanguage(fence.Lang))
            {
                continue;
            }

            var candidate = fence.Body.Trim('\r', '\n');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                unifiedDiff = ClipToChars(candidate, 64000);
                return true;
            }
        }

        var beginPatch = rawModelOutput.IndexOf("*** Begin Patch", StringComparison.Ordinal);
        if (beginPatch >= 0)
        {
            var endPatch = rawModelOutput.IndexOf("*** End Patch", beginPatch, StringComparison.Ordinal);
            if (endPatch > beginPatch)
            {
                unifiedDiff = ClipToChars(rawModelOutput[beginPatch..(endPatch + "*** End Patch".Length)], 64000);
                return true;
            }
        }

        var start = rawModelOutput.IndexOf("\n--- ", StringComparison.Ordinal);
        if (start >= 0)
        {
            var candidate = rawModelOutput[(start + 1)..].Trim();
            if (candidate.Contains("\n+++ ", StringComparison.Ordinal) && candidate.Contains("\n@@", StringComparison.Ordinal))
            {
                unifiedDiff = ClipToChars(candidate, 64000);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractNamedScalarValue(string rawModelOutput, IReadOnlyList<string> keys, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(rawModelOutput) || keys.Count == 0)
        {
            return false;
        }

        var keyExpr = string.Join("|", keys.Select(Regex.Escape));
        var linePattern = $@"(?im)^\s*(?:[-*]\s*)?(?:[""'`])?(?:{keyExpr})(?:[""'`])?\s*[:=]\s*(?<v>.+?)\s*$";
        var lineMatch = Regex.Match(rawModelOutput, linePattern);
        if (lineMatch.Success)
        {
            var extracted = lineMatch.Groups["v"].Value.Trim().Trim('"', '\'', '`');
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                value = extracted;
                return true;
            }
        }

        var jsonPattern = $"(?is)\\\"(?:{keyExpr})\\\"\\s*:\\s*\\\"(?<v>[^\\\"]{{1,5000}})\\\"";
        var jsonMatch = Regex.Match(rawModelOutput, jsonPattern);
        if (jsonMatch.Success)
        {
            var extracted = jsonMatch.Groups["v"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                value = extracted;
                return true;
            }
        }

        var singleQuotedPattern = $"(?is)'(?:{keyExpr})'\\s*:\\s*'(?<v>[^']{{1,5000}})'";
        var singleQuotedMatch = Regex.Match(rawModelOutput, singleQuotedPattern);
        if (singleQuotedMatch.Success)
        {
            var extracted = singleQuotedMatch.Groups["v"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                value = extracted;
                return true;
            }
        }

        return false;
    }

    private static List<(string Lang, string Body)> ExtractCodeFences(string text)
    {
        var result = new List<(string Lang, string Body)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(text, "```(?<lang>[^\\r\\n`]*)\\r?\\n(?<body>[\\s\\S]*?)```"))
        {
            var lang = (match.Groups["lang"].Value ?? string.Empty).Trim();
            var body = match.Groups["body"].Value ?? string.Empty;
            result.Add((lang, body));
        }

        return result;
    }

    private static bool IsShellLanguage(string lang)
    {
        var normalized = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "bash" or "sh" or "zsh" or "shell" or "console" or "cmd" or "bat" or "powershell" or "ps1";
    }

    private static bool IsDiffLanguage(string lang)
    {
        var normalized = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "diff" or "patch";
    }

    private static bool LooksLikeCommandBlock(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var trimmed = body.Trim();
        if (trimmed.Contains('\n'))
        {
            return trimmed.Split('\n').Any(line => line.Contains(' ') || line.Contains('/') || line.Contains('\\'));
        }

        return trimmed.Contains(' ') || trimmed.Contains('/') || trimmed.Contains('\\');
    }

    private static string NormalizeShellCommandBlock(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var lines = body
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                if (line.StartsWith("$ ", StringComparison.Ordinal))
                {
                    return line[2..].Trim();
                }

                if (line.StartsWith("PS> ", StringComparison.OrdinalIgnoreCase))
                {
                    return line[4..].Trim();
                }

                return line;
            })
            .ToList();

        return string.Join('\n', lines);
    }

    private static JsonElement MergeArguments(JsonElement source, IReadOnlyDictionary<string, object?> updates)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                map[property.Name] = property.Value.Clone();
            }
        }

        foreach (var update in updates)
        {
            map[update.Key] = JsonSerializer.SerializeToElement(update.Value);
        }

        var json = JsonSerializer.Serialize(map);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void CapturePathHints(
        List<string> pathHints,
        string workspaceRoot,
        string toolName,
        JsonElement arguments,
        ToolResult result)
    {
        var path = ToolArgumentReader.GetString(arguments, "path");
        var allowMissingPath = toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                               toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
        TrackPathHint(pathHints, workspaceRoot, path, allowMissingPath);

        var pathspec = ToolArgumentReader.GetString(arguments, "pathspec");
        TrackPathHint(pathHints, workspaceRoot, pathspec, true);

        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Take(100))
            {
                if ((toolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
                     toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
                    TryExtractSearchHitPath(line, out var searchPath))
                {
                    TrackPathHint(pathHints, workspaceRoot, searchPath, false);
                    continue;
                }

                if (toolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.StartsWith("[FILE] ", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[DIR] ", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = line.StartsWith("[FILE] ", StringComparison.OrdinalIgnoreCase)
                            ? line["[FILE] ".Length..].Trim()
                            : line["[DIR] ".Length..].Trim();
                        var markerIndex = candidate.IndexOf(" (", StringComparison.Ordinal);
                        if (markerIndex > 0)
                        {
                            candidate = candidate[..markerIndex];
                        }

                        TrackPathHint(pathHints, workspaceRoot, candidate, false);
                    }
                }

                if (TryExtractGenericPathFromLine(line, out var genericPath))
                {
                    TrackPathHint(pathHints, workspaceRoot, genericPath, true);
                }
            }
        }
    }

    private static void TrackPathHint(List<string> pathHints, string workspaceRoot, string? rawPath, bool allowNonExisting)
    {
        if (!TryNormalizePathCandidate(workspaceRoot, rawPath, allowNonExisting, preferFile: false, out var normalized))
        {
            return;
        }

        if (pathHints.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        pathHints.Add(normalized);
        if (pathHints.Count > 64)
        {
            pathHints.RemoveAt(0);
        }
    }

    private static bool TryInferPathFromContext(
        string task,
        string reason,
        string workspaceRoot,
        IReadOnlyList<string> pathHints,
        bool allowNonExisting,
        bool preferFile,
        out string path)
    {
        foreach (var text in new[] { reason, task })
        {
            foreach (var candidate in ExtractPathCandidatesFromText(text))
            {
                if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
                {
                    return true;
                }
            }
        }

        for (var i = pathHints.Count - 1; i >= 0; i--)
        {
            if (TryNormalizePathCandidate(workspaceRoot, pathHints[i], allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (var text in new[] { reason, task })
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\b([A-Za-z0-9_\-]+\.[A-Za-z0-9_]{1,12})\b"))
            {
                var fileName = match.Groups[1].Value;
                if (TryFindUniqueFileByName(workspaceRoot, fileName, out path))
                {
                    return true;
                }
            }
        }

        path = string.Empty;
        return false;
    }

    private static IEnumerable<string> ExtractPathCandidatesFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "[`\"'](?<path>[^`\"'\\r\\n]{1,260})[`\"']"))
        {
            var candidate = match.Groups["path"].Value;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (Match match in Regex.Matches(text, @"(?<![\w/\\])(?<path>[A-Za-z0-9_\-./\\]{2,260}\.[A-Za-z0-9_\-]{1,12})(?![\w/\\])"))
        {
            var candidate = match.Groups["path"].Value;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool TryNormalizePathCandidate(
        string workspaceRoot,
        string? rawCandidate,
        bool allowNonExisting,
        bool preferFile,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return false;
        }

        var candidate = rawCandidate.Trim()
            .Trim('"', '\'', '`')
            .TrimEnd('.', ',', ';', ':', ')', ']', '}');

        if (candidate.Length == 0 || candidate.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var lineSuffixed = Regex.Match(candidate, @"^(?<path>.+\.[A-Za-z0-9_]{1,12}):\d+$");
        if (lineSuffixed.Success)
        {
            candidate = lineSuffixed.Groups["path"].Value;
        }

        if (candidate.Contains('\n') || candidate.Contains('\r'))
        {
            return false;
        }

        string absolute;
        if (allowNonExisting)
        {
            var root = Path.GetFullPath(workspaceRoot);
            absolute = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate));
            if (!PathSafety.IsWithinWorkspace(root, absolute))
            {
                return false;
            }

            if (preferFile &&
                (candidate.EndsWith("/", StringComparison.Ordinal) || candidate.EndsWith("\\", StringComparison.Ordinal)))
            {
                return false;
            }
        }
        else
        {
            try
            {
                absolute = PathSafety.ResolveInWorkspace(workspaceRoot, candidate);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(absolute) && !Directory.Exists(absolute))
            {
                return false;
            }

            if (preferFile && Directory.Exists(absolute))
            {
                return false;
            }
        }

        normalized = Path.GetRelativePath(workspaceRoot, absolute).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = ".";
        }

        if (preferFile && normalized == ".")
        {
            return false;
        }

        return true;
    }

    private static bool TryFindUniqueFileByName(string workspaceRoot, string fileName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string? match = null;
        foreach (var file in Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            if (ShouldSkipPathScan(relative))
            {
                continue;
            }

            if (match is not null)
            {
                return false;
            }

            match = relative;
        }

        if (match is null)
        {
            return false;
        }

        path = match;
        return true;
    }

    private static bool ShouldSkipPathScan(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/');
        return rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith(".evoloop/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractSearchHitPath(string line, out string path)
    {
        var match = Regex.Match(line, @"^(?<path>.+?):\d+\s");
        if (match.Success)
        {
            path = match.Groups["path"].Value.Trim();
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static bool TryExtractGenericPathFromLine(string line, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var statusLine = Regex.Match(trimmed, @"^(?:[ MADRCU\?]{1,3})\s+(?<path>.+)$");
        if (statusLine.Success)
        {
            var candidate = statusLine.Groups["path"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) &&
                (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('.')))
            {
                path = candidate;
                return true;
            }
        }

        var diffHeader = Regex.Match(trimmed, @"^diff --git a/(?<path>.+?) b/.+$");
        if (diffHeader.Success)
        {
            path = diffHeader.Groups["path"].Value.Trim();
            return true;
        }

        var patchHeader = Regex.Match(trimmed, @"^(?:\+\+\+|---)\s+[ab]/(?<path>.+)$");
        if (patchHeader.Success)
        {
            path = patchHeader.Groups["path"].Value.Trim();
            return true;
        }

        var quoted = Regex.Match(trimmed, "[`\"'](?<path>[^`\"'\\r\\n]{1,260})[`\"']");
        if (quoted.Success)
        {
            var candidate = quoted.Groups["path"].Value.Trim();
            if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('.'))
            {
                path = candidate;
                return true;
            }
        }

        var token = Regex.Match(trimmed, @"(?<![\w])(?<path>(?:\.{1,2}|[A-Za-z0-9_\-]+)(?:[/\\][A-Za-z0-9_.\-]+)+[/\\]?)(?![\w])");
        if (token.Success)
        {
            path = token.Groups["path"].Value.Trim();
            return true;
        }

        return false;
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
        ref IModelClient modelClient,
        ref string modelName,
        string workspaceRoot,
        string sessionId,
        ref ToolContext context)
    {
        if (profileIndex + 1 >= profilePlan.Count)
        {
            return false;
        }

        profileIndex++;
        currentProfileName = profilePlan[profileIndex];
        modelClient = _modelRouter.GetClient(currentProfileName);
        modelName = _modelRouter.ResolveModelName(currentProfileName);
        context = _contextFactory.Create(workspaceRoot, sessionId, currentProfileName);
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

internal enum AgentDecisionType
{
    Tool,
    Final,
    Clarify,
    Invalid
}

internal sealed record AgentDecision(AgentDecisionType Type, string ToolName, JsonElement Arguments, string Reason, string Message)
{
    public static AgentDecision Final(string message)
    {
        return new AgentDecision(AgentDecisionType.Final, string.Empty, default, string.Empty, message);
    }

    public static AgentDecision Invalid(string message)
    {
        return new AgentDecision(AgentDecisionType.Invalid, string.Empty, default, string.Empty, message);
    }
}

internal static class AgentDecisionParser
{
    private static readonly JsonDocument EmptyObject = JsonDocument.Parse("{}");

    public static AgentDecision Parse(string content, IEnumerable<string>? toolNames = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AgentDecision.Invalid("Empty model response.");
        }

        if (!TryParseJson(content, out var document))
        {
            var recovered = TryParseFromText(content, toolNames);
            return recovered ?? AgentDecision.Invalid("Response is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            var allowedTools = toolNames is null
                ? null
                : new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
            if (TryParseToolCallVariants(root, allowedTools, out var variantDecision))
            {
                return variantDecision;
            }

            if (!root.TryGetProperty("type", out var typeEl))
            {
                return AgentDecision.Invalid("JSON missing required 'type' property.");
            }

            var type = typeEl.GetString()?.Trim().ToLowerInvariant();
            return type switch
            {
                "final" => AgentDecision.Final(root.TryGetProperty("message", out var finalMsg) ? finalMsg.GetString() ?? string.Empty : string.Empty),
                "clarify" => new AgentDecision(
                    AgentDecisionType.Clarify,
                    string.Empty,
                    EmptyObject.RootElement.Clone(),
                    string.Empty,
                    root.TryGetProperty("message", out var clarifyMsg) ? clarifyMsg.GetString() ?? string.Empty : string.Empty),
                "tool" => new AgentDecision(
                    AgentDecisionType.Tool,
                    root.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("arguments", out var argsEl) ? NormalizeArguments(argsEl) : EmptyObject.RootElement.Clone(),
                    root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? string.Empty : string.Empty,
                    string.Empty),
                _ => AgentDecision.Invalid("Unknown decision type.")
            };
        }
    }

    private static bool TryParseJson(string content, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(content);
            return true;
        }
        catch
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var candidate = content[start..(end + 1)];
                try
                {
                    document = JsonDocument.Parse(candidate);
                    return true;
                }
                catch
                {
                    // ignored
                }
            }
        }

        document = null!;
        return false;
    }

    private static bool TryParseToolCallVariants(
        JsonElement root,
        HashSet<string>? allowedTools,
        out AgentDecision decision)
    {
        return TryParseToolCallVariants(root, allowedTools, 0, out decision);
    }

    private static bool TryParseToolCallVariants(
        JsonElement root,
        HashSet<string>? allowedTools,
        int depth,
        out AgentDecision decision)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            decision = AgentDecision.Invalid("Root JSON is not an object.");
            return false;
        }

        if (depth > 2)
        {
            decision = AgentDecision.Invalid("Tool-call variant nesting too deep.");
            return false;
        }

        if (TryBuildToolDecision(root, "tool", allowedTools, out decision))
        {
            return true;
        }

        if (TryBuildToolDecision(root, "action", allowedTools, out decision))
        {
            return true;
        }

        if (TryBuildToolDecision(root, "name", allowedTools, out decision))
        {
            return true;
        }

        if (root.TryGetProperty("function_call", out var functionCall) &&
            functionCall.ValueKind == JsonValueKind.Object &&
            TryBuildToolDecision(functionCall, "name", allowedTools, out decision))
        {
            return true;
        }

        if (root.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (call.TryGetProperty("function", out var functionObj) &&
                    functionObj.ValueKind == JsonValueKind.Object &&
                    TryBuildToolDecision(functionObj, "name", allowedTools, out decision))
                {
                    return true;
                }

                if (TryBuildToolDecision(call, "name", allowedTools, out decision))
                {
                    return true;
                }
            }
        }

        if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString() ?? string.Empty;
            if ((type.Equals("tool_call", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("function_call", StringComparison.OrdinalIgnoreCase)) &&
                TryBuildToolDecision(root, "name", allowedTools, out decision))
            {
                return true;
            }
        }

        var nestedKeys = new[] { "decision", "response", "output", "result" };
        foreach (var key in nestedKeys)
        {
            if (!root.TryGetProperty(key, out var nested))
            {
                continue;
            }

            if (nested.ValueKind == JsonValueKind.Object &&
                TryParseToolCallVariants(nested, allowedTools, depth + 1, out decision))
            {
                return true;
            }

            if (nested.ValueKind == JsonValueKind.String &&
                TryParseJson(nested.GetString() ?? string.Empty, out var nestedDoc))
            {
                using (nestedDoc)
                {
                    if (TryParseToolCallVariants(nestedDoc.RootElement, allowedTools, depth + 1, out decision))
                    {
                        return true;
                    }
                }
            }
        }

        if (root.TryGetProperty("content", out var contentEl) &&
            contentEl.ValueKind == JsonValueKind.String &&
            TryParseJson(contentEl.GetString() ?? string.Empty, out var contentDoc))
        {
            using (contentDoc)
            {
                if (TryParseToolCallVariants(contentDoc.RootElement, allowedTools, depth + 1, out decision))
                {
                    return true;
                }
            }
        }

        if (!root.TryGetProperty("type", out _) &&
            root.TryGetProperty("message", out var messageEl) &&
            messageEl.ValueKind == JsonValueKind.String)
        {
            decision = AgentDecision.Final(messageEl.GetString() ?? string.Empty);
            return true;
        }

        decision = AgentDecision.Invalid("No known tool-call variant.");
        return false;
    }

    private static bool TryBuildToolDecision(
        JsonElement root,
        string key,
        HashSet<string>? allowedTools,
        out AgentDecision decision)
    {
        if (!root.TryGetProperty(key, out var toolEl) || toolEl.ValueKind != JsonValueKind.String)
        {
            decision = AgentDecision.Invalid("Tool key not found.");
            return false;
        }

        var toolName = toolEl.GetString() ?? string.Empty;
        if (allowedTools is not null && allowedTools.Count > 0 && !allowedTools.Contains(toolName))
        {
            decision = AgentDecision.Invalid("Tool name is not allowed.");
            return false;
        }

        if (key.Equals("name", StringComparison.OrdinalIgnoreCase) &&
            !root.TryGetProperty("arguments", out _) &&
            !root.TryGetProperty("args", out _) &&
            !root.TryGetProperty("action_input", out _) &&
            !root.TryGetProperty("input", out _))
        {
            decision = AgentDecision.Invalid("Name key is not a tool call.");
            return false;
        }

        var args = root.TryGetProperty("arguments", out var argsEl) ? NormalizeArguments(argsEl) :
            root.TryGetProperty("args", out var argsAltEl) ? NormalizeArguments(argsAltEl) :
            root.TryGetProperty("action_input", out var actionInputEl) ? NormalizeArguments(actionInputEl) :
            root.TryGetProperty("input", out var inputEl) ? NormalizeArguments(inputEl) :
            EmptyObject.RootElement.Clone();

        var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString() ?? "recovered tool decision"
            : "recovered tool decision";

        decision = new AgentDecision(
            AgentDecisionType.Tool,
            toolName,
            args,
            reason,
            string.Empty);
        return true;
    }

    private static AgentDecision? TryParseFromText(string content, IEnumerable<string>? toolNames)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var finalMatch = Regex.Match(trimmed, @"^\s*(final|done|completed)\s*[:\-]\s*(.+)$", RegexOptions.IgnoreCase);
        if (finalMatch.Success)
        {
            return AgentDecision.Final(finalMatch.Groups[2].Value.Trim());
        }

        var toolName = TryFindToolName(trimmed, toolNames);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        var arguments = TryExtractArgumentsObject(trimmed) ?? EmptyObject.RootElement.Clone();
        var reason = "recovered from non-JSON model output";
        return new AgentDecision(AgentDecisionType.Tool, toolName, arguments, reason, string.Empty);
    }

    private static string? TryFindToolName(string text, IEnumerable<string>? toolNames)
    {
        var toolList = toolNames?.ToList() ?? new List<string>();
        if (toolList.Count == 0)
        {
            return null;
        }

        var actionTag = Regex.Match(text, @"(?im)^\s*(tool|action)\s*[:=]\s*([a-zA-Z0-9_\-]+)\s*$");
        if (actionTag.Success)
        {
            var candidate = actionTag.Groups[2].Value.Trim();
            if (toolList.Any(t => t.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return toolList.First(t => t.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            }
        }

        foreach (var tool in toolList.OrderByDescending(t => t.Length))
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(tool)}\b", RegexOptions.IgnoreCase))
            {
                return tool;
            }
        }

        return null;
    }

    private static JsonElement? TryExtractArgumentsObject(string text)
    {
        var argsTag = Regex.Match(text, @"(?is)(arguments|args)\s*[:=]\s*(\{.*\})");
        if (argsTag.Success)
        {
            var fromTag = argsTag.Groups[2].Value;
            if (TryParseJson(fromTag, out var docFromTag))
            {
                using (docFromTag)
                {
                    return docFromTag.RootElement.Clone();
                }
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var candidate = text[start..(end + 1)];
            if (TryParseJson(candidate, out var doc))
            {
                using (doc)
                {
                    return doc.RootElement.Clone();
                }
            }
        }

        return null;
    }

    private static JsonElement NormalizeArguments(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            return source.Clone();
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            var str = source.GetString() ?? string.Empty;
            if (TryParseJson(str, out var doc))
            {
                using (doc)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        return doc.RootElement.Clone();
                    }
                }
            }

            using var wrapped = JsonDocument.Parse($"{{\"input\":{JsonSerializer.Serialize(str)}}}");
            return wrapped.RootElement.Clone();
        }

        var raw = source.GetRawText();
        using var wrappedFallback = JsonDocument.Parse($"{{\"input\":{raw}}}");
        return wrappedFallback.RootElement.Clone();
    }
}
