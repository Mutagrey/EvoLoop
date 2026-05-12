using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Storage;

public sealed class JsonlEventStore : IEventStore
{
    private readonly string _sessionsPath;
    private readonly string _stepsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonlEventStore(string workspaceRoot)
    {
        var storageRoot = Path.Combine(workspaceRoot, ".evoloop", "storage");
        Directory.CreateDirectory(storageRoot);
        _sessionsPath = Path.Combine(storageRoot, "sessions.jsonl");
        _stepsPath = Path.Combine(storageRoot, "steps.jsonl");
    }

    public async Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
    {
        var session = new SessionInfo(Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow);
        await AppendSessionStartAsync(session, workspaceRoot, profile, task, ct);
        return session;
    }

    internal async Task AppendSessionStartAsync(SessionInfo session, string workspaceRoot, string profile, string task, CancellationToken ct)
    {
        var payload = new
        {
            type = AgentEventTypes.SessionStart,
            sessionId = session.SessionId,
            startedAtUtc = session.StartedAtUtc,
            workspaceRoot,
            profile,
            task
        };

        await AppendJsonLineAsync(_sessionsPath, payload, ct);
    }

    public Task AppendStepAsync(SessionStep step, CancellationToken ct)
    {
        return AppendJsonLineAsync(_stepsPath, step, ct);
    }

    public Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct)
    {
        var payload = new
        {
            type = AgentEventTypes.SessionEnd,
            sessionId,
            finalStatus,
            completedAtUtc = DateTimeOffset.UtcNow
        };

        return AppendJsonLineAsync(_sessionsPath, payload, ct);
    }

    private async Task AppendJsonLineAsync(string path, object payload, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;
        await _lock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
