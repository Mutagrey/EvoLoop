using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Storage;

public sealed partial class WorkspaceMemoryStore : IWorkspaceMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly AgentConfig _config;
    private readonly string _workspaceRoot;
    private readonly string _workspaceRootHash;
    private readonly string _projectId;
    private readonly string _runsPath;
    private readonly string? _portableRunsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceMemoryStore(string workspaceRoot, AgentConfig config)
    {
        _config = config;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _workspaceRootHash = ComputeSha256Hex(NormalizePath(_workspaceRoot));
        _projectId = ResolveOrCreateProjectIdentity(_workspaceRoot);

        var storageRoot = Path.Combine(_workspaceRoot, ".evoloop", "storage");
        Directory.CreateDirectory(storageRoot);
        _runsPath = Path.Combine(storageRoot, "memory-runs.jsonl");
        _portableRunsPath = TryResolvePortableRunsPath(_projectId);
    }

    public async Task<WorkspaceMemoryContext> LoadContextAsync(string workspaceRoot, string task, CancellationToken ct)
    {
        if (!_config.Runtime.MemoryEnabled)
        {
            return WorkspaceMemoryContext.Empty;
        }

        var entries = await LoadEntriesAsync(ct);
        if (entries.Count == 0)
        {
            return WorkspaceMemoryContext.Empty;
        }

        var limit = Math.Max(1, _config.Runtime.MemoryMaxRuns);
        var ranked = RankEntries(entries, task).Take(limit).ToList();
        if (ranked.Count == 0)
        {
            return WorkspaceMemoryContext.Empty;
        }

        var maxChars = Math.Max(800, _config.Runtime.MemoryContextMaxChars);
        var sb = new StringBuilder();
        sb.AppendLine("WORKSPACE MEMORY (from previous runs, use only if relevant):");

        var used = 0;
        foreach (var entry in ranked)
        {
            if (!ShouldIncludeInContext(entry, task))
            {
                continue;
            }

            var line = BuildContextLine(entry);
            if (sb.Length + line.Length + Environment.NewLine.Length > maxChars)
            {
                break;
            }

            sb.AppendLine(line);
            used++;
        }

        if (used == 0)
        {
            return WorkspaceMemoryContext.Empty;
        }

        return new WorkspaceMemoryContext(sb.ToString().TrimEnd(), used);
    }

    public async Task SaveRunAsync(WorkspaceMemoryRecord record, CancellationToken ct)
    {
        if (!_config.Runtime.MemoryEnabled)
        {
            return;
        }

        var entry = BuildEntry(
            record,
            _config.Runtime.ObservationMaxChars,
            _projectId,
            _workspaceRoot,
            _workspaceRootHash);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _gate.WaitAsync(ct);
        try
        {
            await AppendLineAsync(_runsPath, line, ct);
            await PruneIfNeededAsync(_runsPath, ct);

            if (!string.IsNullOrWhiteSpace(_portableRunsPath) &&
                !_portableRunsPath.Equals(_runsPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await AppendLineAsync(_portableRunsPath, line, ct);
                    await PruneIfNeededAsync(_portableRunsPath, ct);
                }
                catch
                {
                    // Portable mirror is best-effort and should never fail the run.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<MemoryRunEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        var merged = new Dictionary<string, MemoryRunEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in EnumerateSources())
        {
            var entries = await LoadEntriesFromPathAsync(source.Path, ct);
            foreach (var entry in entries)
            {
                if (!EntryBelongsToCurrentProject(entry, source.AllowLegacyWithoutIdentity))
                {
                    continue;
                }

                if (merged.TryGetValue(entry.SessionId, out var existing))
                {
                    if (entry.CompletedAtUtc > existing.CompletedAtUtc)
                    {
                        merged[entry.SessionId] = entry;
                    }
                }
                else
                {
                    merged[entry.SessionId] = entry;
                }
            }
        }

        return merged.Values
            .OrderByDescending(e => e.CompletedAtUtc)
            .ToList();
    }

    private IEnumerable<(string Path, bool AllowLegacyWithoutIdentity)> EnumerateSources()
    {
        yield return (_runsPath, true);

        if (!string.IsNullOrWhiteSpace(_portableRunsPath) &&
            !_portableRunsPath.Equals(_runsPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return (_portableRunsPath, false);
        }
    }

    private async Task<List<MemoryRunEntry>> LoadEntriesFromPathAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new List<MemoryRunEntry>();
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        var entries = new List<MemoryRunEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<MemoryRunEntry>(line, JsonOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.SessionId))
                {
                    continue;
                }

                parsed = parsed with
                {
                    Task = parsed.Task ?? string.Empty,
                    FinalMessage = parsed.FinalMessage ?? string.Empty,
                    Summary = parsed.Summary ?? string.Empty,
                    Highlights = parsed.Highlights ?? Array.Empty<string>(),
                    ProjectId = parsed.ProjectId ?? string.Empty,
                    WorkspaceRoot = parsed.WorkspaceRoot ?? string.Empty,
                    WorkspaceRootHash = parsed.WorkspaceRootHash ?? string.Empty
                };

                entries.Add(parsed);
            }
            catch
            {
                // ignore malformed lines
            }
        }

        return entries;
    }

    private bool EntryBelongsToCurrentProject(MemoryRunEntry entry, bool allowLegacyWithoutIdentity)
    {
        if (!string.IsNullOrWhiteSpace(entry.ProjectId))
        {
            return entry.ProjectId.Equals(_projectId, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(entry.WorkspaceRootHash))
        {
            return entry.WorkspaceRootHash.Equals(_workspaceRootHash, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(entry.WorkspaceRoot))
        {
            try
            {
                var normalized = NormalizePath(Path.GetFullPath(entry.WorkspaceRoot));
                return normalized.Equals(NormalizePath(_workspaceRoot), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        return allowLegacyWithoutIdentity;
    }

    private async Task AppendLineAsync(string path, string line, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.AppendAllTextAsync(path, line, Encoding.UTF8, ct);
    }

    private async Task PruneIfNeededAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        var maxLines = Math.Max(200, _config.Runtime.MemoryMaxRuns * 20);
        if (lines.Length <= maxLines)
        {
            return;
        }

        var keep = lines.Skip(lines.Length - maxLines).ToArray();
        await File.WriteAllLinesAsync(path, keep, Encoding.UTF8, ct);
    }

    private sealed record ProjectIdentityDocument(
        string ProjectId,
        string CreatedAtUtc,
        string FirstWorkspaceRoot,
        string LastWorkspaceRoot,
        string Derivation);

    private sealed record MemoryRunEntry(
        string SessionId,
        DateTimeOffset CompletedAtUtc,
        string Task,
        bool Success,
        string FinalMessage,
        string Summary,
        string[] Highlights,
        double RankScore,
        string ProjectId,
        string WorkspaceRoot,
        string WorkspaceRootHash);
}
