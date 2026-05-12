using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Storage;

public sealed class JsonlEventLog : IEventLog
{
    private readonly string _eventsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonlEventLog(string workspaceRoot)
    {
        var storageRoot = Path.Combine(workspaceRoot, ".evoloop", "storage");
        Directory.CreateDirectory(storageRoot);
        _eventsPath = Path.Combine(storageRoot, "events.jsonl");
    }

    public async Task AppendAsync(AgentEventRecord evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(evt) + Environment.NewLine;
        await _lock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_eventsPath, payload, Encoding.UTF8, ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
