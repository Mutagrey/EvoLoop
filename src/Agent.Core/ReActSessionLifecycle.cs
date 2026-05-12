namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private async Task CompleteRunLifecycleAsync(
        AgentRunRequest request,
        IAgentRunObserver observer,
        SessionInfo session,
        ToolContext context,
        IReadOnlyList<SessionStep> trace,
        bool success,
        string finalMessage,
        CancellationToken ct)
    {
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
            AgentEventTypes.FinalAnswer,
            DateTimeOffset.UtcNow,
            finalMessage,
            null,
            success), ct);
        await context.EventLog.AppendAsync(new AgentEventRecord(
            session.SessionId,
            AgentEventTypes.SessionEnd,
            DateTimeOffset.UtcNow,
            success ? "completed" : "incomplete",
            null,
            success), ct);
    }

    private async Task<AgentRunResult> CompleteErrorLifecycleAsync(
        AgentRunRequest request,
        IAgentRunObserver observer,
        SessionInfo session,
        ToolContext context,
        IReadOnlyList<SessionStep> trace,
        Exception ex,
        CancellationToken ct)
    {
        await _eventStore.CompleteSessionAsync(session.SessionId, "error", ct);
        await context.EventLog.AppendAsync(new AgentEventRecord(
            session.SessionId,
            AgentEventTypes.SessionEnd,
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
