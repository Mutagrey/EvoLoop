using System.Diagnostics;

namespace Agent.Core;

internal sealed class DefaultToolTurnExecutor : IToolTurnExecutor
{
    public async Task<ToolTurnExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct)
    {
        var policyDecision = request.PolicyEngine.Evaluate(request.Call, request.Context);
        if (policyDecision.Kind == PolicyDecisionKind.Deny)
        {
            await request.Observer.OnEventAsync(new AgentRunEvent(
                AgentRunEventType.PolicyDenied,
                policyDecision.Reason,
                request.Step,
                request.Tool.Name), ct);

            await request.Context.EventLog.AppendAsync(new AgentEventRecord(
                request.Context.SessionId,
                AgentEventTypes.PolicyDenied,
                DateTimeOffset.UtcNow,
                policyDecision.Reason,
                request.Tool.Name,
                false), ct);

            return new ToolTurnExecutionResult(false, false, null, null, $"OBSERVATION: Policy denied this action: {policyDecision.Reason}");
        }

        if (policyDecision.Kind == PolicyDecisionKind.RequireApproval)
        {
            await request.Observer.OnEventAsync(new AgentRunEvent(
                AgentRunEventType.ApprovalRequired,
                policyDecision.Reason,
                request.Step,
                request.Tool.Name), ct);

            await request.Context.EventLog.AppendAsync(new AgentEventRecord(
                request.Context.SessionId,
                AgentEventTypes.ApprovalRequest,
                DateTimeOffset.UtcNow,
                policyDecision.Reason,
                request.Tool.Name,
                null), ct);

            var approved = await request.ApprovalService.RequestApprovalAsync(new ApprovalRequest(
                request.Tool.Name,
                policyDecision.Reason,
                request.Call.Arguments.ToString()), ct);

            await request.Context.EventLog.AppendAsync(new AgentEventRecord(
                request.Context.SessionId,
                AgentEventTypes.ApprovalResult,
                DateTimeOffset.UtcNow,
                approved ? "approved" : "rejected",
                request.Tool.Name,
                approved), ct);

            if (!approved)
            {
                await request.Observer.OnEventAsync(new AgentRunEvent(
                    AgentRunEventType.ApprovalRejected,
                    "User rejected tool execution.",
                    request.Step,
                    request.Tool.Name), ct);

                return new ToolTurnExecutionResult(false, false, null, null, "OBSERVATION: Approval rejected by user.");
            }

            await request.Observer.OnEventAsync(new AgentRunEvent(
                AgentRunEventType.ApprovalGranted,
                "User approved tool execution.",
                request.Step,
                request.Tool.Name), ct);
        }

        await request.Observer.OnEventAsync(new AgentRunEvent(
            AgentRunEventType.ToolExecutionStarted,
            $"Running tool {request.Tool.Name}",
            request.Step,
            request.Tool.Name), ct);

        await request.Context.EventLog.AppendAsync(new AgentEventRecord(
            request.Context.SessionId,
            AgentEventTypes.ToolCall,
            DateTimeOffset.UtcNow,
            request.Call.Reason,
            request.Tool.Name,
            null), ct);

        ToolResult result;
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.Context.Config.Runtime.ToolTimeoutSeconds));
        try
        {
            result = await request.Tool.ExecuteAsync(request.Call, request.Context, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new ToolResult(false, $"Tool timed out after {request.Context.Config.Runtime.ToolTimeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            result = new ToolResult(false, $"Tool threw exception: {ex.Message}");
        }
        stopwatch.Stop();
        var activity = ToolActivityMetadata.Build(request.Tool.Name, request.Call.Arguments, result);
        var completionMessage = activity.TryGetValue(ToolActivityMetadata.SummaryKey, out var summary)
            ? summary
            : result.Message;

        var stepRecord = new SessionStep(
            request.Context.SessionId,
            request.Step,
            request.Action,
            request.Tool.Name,
            request.Reasoning,
            result.Success,
            result.StdOut ?? result.Message,
            DateTimeOffset.UtcNow,
            stopwatch.ElapsedMilliseconds,
            result.Success ? null : result.StdErr ?? result.Message);

        await request.EventStore.AppendStepAsync(stepRecord, ct);
        await request.Context.EventLog.AppendAsync(new AgentEventRecord(
            request.Context.SessionId,
            AgentEventTypes.ToolResult,
            DateTimeOffset.UtcNow,
            result.Message,
            request.Tool.Name,
            result.Success,
            activity), ct);

        await request.Observer.OnEventAsync(new AgentRunEvent(
            AgentRunEventType.ToolExecutionCompleted,
            completionMessage,
            request.Step,
            request.Tool.Name,
            activity), ct);

        return new ToolTurnExecutionResult(true, result.Success, result, stepRecord);
    }
}
