using Agent.Core;

namespace Agent.Storage;

public sealed class HybridEventStore : IEventStore
{
    private readonly IEventStore _primary;
    private readonly IEventStore? _secondary;

    public HybridEventStore(string workspaceRoot)
    {
        _primary = new JsonlEventStore(workspaceRoot);
        _secondary = SqliteCliEventStore.IsAvailable()
            ? new SqliteCliEventStore(workspaceRoot)
            : null;
    }

    public Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
        => StartSessionInternalAsync(workspaceRoot, profile, task, ct);

    public Task AppendStepAsync(SessionStep step, CancellationToken ct)
        => AppendStepInternalAsync(step, ct);

    public Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct)
        => CompleteSessionInternalAsync(sessionId, finalStatus, ct);

    private async Task<SessionInfo> StartSessionInternalAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
    {
        var session = await _primary.StartSessionAsync(workspaceRoot, profile, task, ct);
        if (_secondary is SqliteCliEventStore sqlite)
        {
            await sqlite.StartSessionProjectionAsync(session, workspaceRoot, profile, task, ct);
        }

        return session;
    }

    private async Task AppendStepInternalAsync(SessionStep step, CancellationToken ct)
    {
        await _primary.AppendStepAsync(step, ct);
        if (_secondary is not null)
        {
            await _secondary.AppendStepAsync(step, ct);
        }
    }

    private async Task CompleteSessionInternalAsync(string sessionId, string finalStatus, CancellationToken ct)
    {
        await _primary.CompleteSessionAsync(sessionId, finalStatus, ct);
        if (_secondary is not null)
        {
            await _secondary.CompleteSessionAsync(sessionId, finalStatus, ct);
        }
    }
}
