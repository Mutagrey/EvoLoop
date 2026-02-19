using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

        var context = _contextFactory.Create(request.WorkspaceRoot, session.SessionId, request.ProfileName);
        var modelClient = _modelRouter.GetClient(request.ProfileName);
        var modelName = _modelRouter.ResolveModelName(request.ProfileName);

        var history = new List<ModelMessage>
        {
            new("user", request.Task)
        };

        string finalMessage = "Agent ended without final answer.";
        bool success = false;

        try
        {
            for (var step = 1; step <= maxSteps; step++)
            {
                await observer.OnEventAsync(new AgentRunEvent(AgentRunEventType.ModelCallStarted, "Analyzing next action", step), ct);

                var modelRequest = new ModelTurnRequest(
                    request.ProfileName,
                    modelName,
                    BuildSystemPrompt(_tools.Values),
                    history,
                    GetTemperature(request.ProfileName),
                    GetMaxTokens(request.ProfileName),
                    new Dictionary<string, string>
                    {
                        ["session_id"] = session.SessionId,
                        ["step"] = step.ToString()
                    });

                var modelResult = await modelClient.CompleteAsync(modelRequest, ct);

                await observer.OnEventAsync(new AgentRunEvent(AgentRunEventType.ModelCallCompleted, "Model response received", step), ct);

                var decision = AgentDecisionParser.Parse(modelResult.Content);
                if (decision.Type == AgentDecisionType.Final)
                {
                    finalMessage = decision.Message;
                    success = true;
                    break;
                }

                if (decision.Type == AgentDecisionType.Clarify)
                {
                    finalMessage = decision.Message;
                    success = false;
                    break;
                }

                if (string.IsNullOrWhiteSpace(decision.ToolName) || !_tools.TryGetValue(decision.ToolName, out var tool))
                {
                    var invalidTool = decision.ToolName ?? "<none>";
                    history.Add(new ModelMessage("assistant", modelResult.Content));
                    history.Add(new ModelMessage("user", $"OBSERVATION: Unknown tool '{invalidTool}'. Use one of: {string.Join(", ", _tools.Keys.OrderBy(x => x))}."));
                    continue;
                }

                await observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ToolDecision,
                    $"Tool selected: {tool.Name}",
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
                    "Executing tool",
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
                    result.Success ? "Tool completed" : "Tool failed",
                    step,
                    tool.Name), ct);

                var output = TruncateOutput(result.StdOut ?? result.Message, _config.Runtime.MaxOutputBytes);
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
        return _config.Models.TryGetValue(profileName, out var profile) ? profile.Temperature : 0.2;
    }

    private int GetMaxTokens(string profileName)
    {
        return _config.Models.TryGetValue(profileName, out var profile) ? profile.MaxTokens : 1200;
    }

    private static string BuildSystemPrompt(IEnumerable<ITool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an autonomous coding CLI agent.");
        sb.AppendLine("Work in a ReAct style: analyze, choose exactly one tool, observe result, and repeat.");
        sb.AppendLine("Return STRICT JSON only, no markdown.");
        sb.AppendLine("Schema:");
        sb.AppendLine("For tool call: {\"type\":\"tool\",\"tool\":\"tool_name\",\"reason\":\"why\",\"arguments\":{...}}");
        sb.AppendLine("For final response: {\"type\":\"final\",\"message\":\"...\"}");
        sb.AppendLine("For clarification request: {\"type\":\"clarify\",\"message\":\"...\"}");
        sb.AppendLine("Available tools:");

        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
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
        sb.AppendLine("- If task is done, return final.");
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
}

internal enum AgentDecisionType
{
    Tool,
    Final,
    Clarify
}

internal sealed record AgentDecision(AgentDecisionType Type, string ToolName, JsonElement Arguments, string Reason, string Message)
{
    public static AgentDecision Final(string message)
    {
        return new AgentDecision(AgentDecisionType.Final, string.Empty, default, string.Empty, message);
    }
}

internal static class AgentDecisionParser
{
    private static readonly JsonDocument EmptyObject = JsonDocument.Parse("{}");

    public static AgentDecision Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AgentDecision.Final("Empty model response.");
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
            {
                return AgentDecision.Final(content.Trim());
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
                _ => AgentDecision.Final(content.Trim())
            };
        }
        catch
        {
            return AgentDecision.Final(content.Trim());
        }
    }
}
