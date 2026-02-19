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
        AgentConfig config)
    {
        _modelRouter = modelRouter;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _policyEngine = policyEngine;
        _approvalService = approvalService;
        _eventStore = eventStore;
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

        var history = new List<ModelMessage>
        {
            new("user", request.Task)
        };
        var requiresToolBeforeFinal = TaskLikelyRequiresTools(request.Task);
        var toolStepsExecuted = 0;
        var consecutiveInvalidResponses = 0;
        var consecutiveFinalWithoutTools = 0;

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

                var modelRequest = new ModelTurnRequest(
                    currentProfileName,
                    modelName,
                    BuildSystemPrompt(_tools.Values),
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
                    consecutiveInvalidResponses++;
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
                        await observer.OnEventAsync(new AgentRunEvent(
                            AgentRunEventType.FinalRejectedRequiresTool,
                            $"Final rejected at step {step}: task requires tool actions first.",
                            step), ct);

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

                    consecutiveFinalWithoutTools = 0;
                    finalMessage = decision.Message;
                    success = true;
                    break;
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

                consecutiveInvalidResponses = 0;
                consecutiveFinalWithoutTools = 0;

                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ToolDecision,
                    BuildToolPlanMessage(tool.Name, decision.Reason, decision.Arguments),
                    step,
                    tool.Name), ct);

                var call = new ToolCall(tool.Name, decision.Arguments, decision.Reason);
                var policyDecision = _policyEngine.Evaluate(call, context);

                if (policyDecision.Kind == PolicyDecisionKind.Deny)
                {
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

                history.Add(new ModelMessage("assistant", modelResult.Content));
                history.Add(new ModelMessage("user", BuildObservationMessage(tool.Name, result)));
            }

            if (!success && finalMessage == "Agent ended without final answer.")
            {
                finalMessage = $"Reached max steps ({maxSteps}) without final answer.";
            }

            await _eventStore.CompleteSessionAsync(session.SessionId, success ? "completed" : "incomplete", ct);

            await observer.OnEventAsync(new AgentRunEvent(
                AgentRunEventType.SessionCompleted,
                success ? "Task completed" : "Task ended without completion"), ct);

            return new AgentRunResult(success, finalMessage, trace.Count, session.SessionId, trace);
        }
        catch (Exception ex)
        {
            await _eventStore.CompleteSessionAsync(session.SessionId, "error", ct);
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

    private static string BuildObservationMessage(string toolName, ToolResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OBSERVATION from {toolName}");
        sb.AppendLine($"success: {result.Success}");
        sb.AppendLine($"message: {result.Message}");

        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine("stdout:");
            sb.AppendLine(result.StdOut);
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            sb.AppendLine("stderr:");
            sb.AppendLine(result.StdErr);
        }

        return sb.ToString();
    }

    private static string BuildToolPlanMessage(string toolName, string reason, JsonElement arguments)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "no reason provided" : ToOneLine(reason, 160);
        return $"Plan {toolName}: {normalizedReason} | args {PreviewArguments(arguments)}";
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

    private AgentDecision? TryCreateBootstrapDecision(
        bool requiresToolBeforeFinal,
        int toolStepsExecuted,
        int consecutiveInvalidResponses,
        string task)
    {
        if (!requiresToolBeforeFinal || toolStepsExecuted > 0 || consecutiveInvalidResponses < 2)
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
            if (TryParseToolCallVariants(root, out var variantDecision))
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
                    root.TryGetProperty("arguments", out var argsEl) ? argsEl.Clone() : EmptyObject.RootElement.Clone(),
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

    private static bool TryParseToolCallVariants(JsonElement root, out AgentDecision decision)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            decision = AgentDecision.Invalid("Root JSON is not an object.");
            return false;
        }

        if (root.TryGetProperty("tool", out var toolEl) && toolEl.ValueKind == JsonValueKind.String)
        {
            var normalizedArgs = root.TryGetProperty("arguments", out var argsEl) ? NormalizeArguments(argsEl) :
                root.TryGetProperty("args", out var argsAltEl) ? NormalizeArguments(argsAltEl) :
                EmptyObject.RootElement.Clone();

            decision = new AgentDecision(
                AgentDecisionType.Tool,
                toolEl.GetString() ?? string.Empty,
                normalizedArgs,
                root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "recovered tool decision" : "recovered tool decision",
                string.Empty);
            return true;
        }

        if (root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.String)
        {
            var normalizedArgs = root.TryGetProperty("action_input", out var actionInputEl) ? NormalizeArguments(actionInputEl) :
                root.TryGetProperty("arguments", out var argsEl) ? NormalizeArguments(argsEl) :
                EmptyObject.RootElement.Clone();

            decision = new AgentDecision(
                AgentDecisionType.Tool,
                actionEl.GetString() ?? string.Empty,
                normalizedArgs,
                root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "recovered action decision" : "recovered action decision",
                string.Empty);
            return true;
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
