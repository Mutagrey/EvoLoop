namespace Agent.Core;

public sealed class NullObserver : IAgentRunObserver
{
    public static readonly NullObserver Instance = new();

    private NullObserver() { }

    public Task OnEventAsync(AgentRunEvent evt, CancellationToken ct) => Task.CompletedTask;
}

public sealed class NullWorkspaceMemoryStore : IWorkspaceMemoryStore
{
    public static readonly NullWorkspaceMemoryStore Instance = new();

    private NullWorkspaceMemoryStore() { }

    public Task<WorkspaceMemoryContext> LoadContextAsync(string workspaceRoot, string task, CancellationToken ct)
        => Task.FromResult(WorkspaceMemoryContext.Empty);

    public Task SaveRunAsync(WorkspaceMemoryRecord record, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class NullEventStore : IEventStore
{
    public static readonly NullEventStore Instance = new();

    private NullEventStore() { }

    public Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
        => Task.FromResult(new SessionInfo(Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow));

    public Task AppendStepAsync(SessionStep step, CancellationToken ct)
        => Task.CompletedTask;

    public Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class NullEventLog : IEventLog
{
    public static readonly NullEventLog Instance = new();

    private NullEventLog() { }

    public Task AppendAsync(AgentEventRecord evt, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class NullPatchService : IPatchService
{
    public static readonly NullPatchService Instance = new();

    private NullPatchService() { }

    public Task<ToolResult> WriteFileAsync(FileWriteRequest request, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));

    public Task<ToolResult> ApplyPatchAsync(FilePatchRequest request, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));

    public Task<ToolResult> DeleteAsync(FileDeleteRequest request, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));

    public Task<ToolResult> UndoLastAsync(string workspaceRoot, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));
}
