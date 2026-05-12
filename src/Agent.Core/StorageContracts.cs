namespace Agent.Core;

public interface IEventStore
{
    Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct);
    Task AppendStepAsync(SessionStep step, CancellationToken ct);
    Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct);
}

public interface IWorkspaceMemoryStore
{
    Task<WorkspaceMemoryContext> LoadContextAsync(string workspaceRoot, string task, CancellationToken ct);
    Task SaveRunAsync(WorkspaceMemoryRecord record, CancellationToken ct);
}

public interface IEventLog
{
    Task AppendAsync(AgentEventRecord evt, CancellationToken ct);
}

public sealed record SessionInfo(string SessionId, DateTimeOffset StartedAtUtc);

public sealed record SessionStep(
    string SessionId,
    int StepNumber,
    string Action,
    string ToolName,
    string Reasoning,
    bool Success,
    string Output,
    DateTimeOffset TimestampUtc,
    long DurationMs,
    string? Error = null);

public sealed record WorkspaceMemoryContext(string Content, int EntriesUsed)
{
    public static readonly WorkspaceMemoryContext Empty = new(string.Empty, 0);
}

public sealed record WorkspaceMemoryRecord(
    string WorkspaceRoot,
    string SessionId,
    string Task,
    bool Success,
    string FinalMessage,
    IReadOnlyList<SessionStep> Steps,
    DateTimeOffset CompletedAtUtc);

public sealed record AgentEventRecord(
    string SessionId,
    string EventType,
    DateTimeOffset TimestampUtc,
    string Message,
    string? ToolName = null,
    bool? Success = null,
    IReadOnlyDictionary<string, string>? Data = null);
