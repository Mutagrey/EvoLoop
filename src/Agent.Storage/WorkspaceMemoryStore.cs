using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Storage;

public sealed class WorkspaceMemoryStore : IWorkspaceMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly AgentConfig _config;
    private readonly string _runsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceMemoryStore(string workspaceRoot, AgentConfig config)
    {
        _config = config;
        var storageRoot = Path.Combine(workspaceRoot, ".evoloop", "storage");
        Directory.CreateDirectory(storageRoot);
        _runsPath = Path.Combine(storageRoot, "memory-runs.jsonl");
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

        var entry = BuildEntry(record, _config.Runtime.ObservationMaxChars);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_runsPath, line, Encoding.UTF8, ct);
            await PruneIfNeededAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<MemoryRunEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (!File.Exists(_runsPath))
        {
            return new List<MemoryRunEntry>();
        }

        var lines = await File.ReadAllLinesAsync(_runsPath, ct);
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
                    Highlights = parsed.Highlights ?? Array.Empty<string>()
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

    private async Task PruneIfNeededAsync(CancellationToken ct)
    {
        if (!File.Exists(_runsPath))
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(_runsPath, ct);
        var maxLines = Math.Max(200, _config.Runtime.MemoryMaxRuns * 20);
        if (lines.Length <= maxLines)
        {
            return;
        }

        var keep = lines.Skip(lines.Length - maxLines).ToArray();
        await File.WriteAllLinesAsync(_runsPath, keep, Encoding.UTF8, ct);
    }

    private static IEnumerable<MemoryRunEntry> RankEntries(IEnumerable<MemoryRunEntry> entries, string task)
    {
        var orderedByRecency = entries
            .OrderByDescending(e => e.CompletedAtUtc)
            .ToList();

        for (var i = 0; i < orderedByRecency.Count; i++)
        {
            var entry = orderedByRecency[i];
            var overlap = ScoreTaskOverlap(task, $"{entry.Task} {entry.Summary}");
            var recencyBoost = 1.0 / (1 + i);
            var outcomeBoost = entry.Success ? 0.05 : 0.0;
            entry = entry with { RankScore = overlap + recencyBoost + outcomeBoost };
            orderedByRecency[i] = entry;
        }

        return orderedByRecency
            .OrderByDescending(e => e.RankScore)
            .ThenByDescending(e => e.CompletedAtUtc);
    }

    private static double ScoreTaskOverlap(string task, string corpus)
    {
        if (string.IsNullOrWhiteSpace(task) || string.IsNullOrWhiteSpace(corpus))
        {
            return 0;
        }

        var words = task
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeToken)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (words.Count == 0)
        {
            return 0;
        }

        var normalizedCorpus = NormalizeToken(corpus);
        var hits = words.Count(w => normalizedCorpus.Contains(w, StringComparison.Ordinal));
        return hits / (double)words.Count;
    }

    private static string BuildContextLine(MemoryRunEntry entry)
    {
        var stamp = entry.CompletedAtUtc.ToString("yyyy-MM-dd HH:mm");
        var status = entry.Success ? "ok" : "failed";
        var task = ToOneLine(entry.Task, 120);
        var summary = ToOneLine(entry.Summary, 200);
        return $"- [{stamp}] {status} task=\"{task}\" | {summary}";
    }

    private static MemoryRunEntry BuildEntry(WorkspaceMemoryRecord record, int summaryMaxChars)
    {
        var toolCounts = record.Steps
            .GroupBy(step => step.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}")
            .Take(8)
            .ToArray();

        var failures = record.Steps
            .Where(step => !step.Success)
            .Select(step => $"{step.ToolName} -> {ToOneLine(step.Error ?? step.Output, 120)}")
            .Take(3)
            .ToArray();

        var highlights = ExtractHighlights(record.Steps)
            .Take(8)
            .ToArray();

        var sb = new StringBuilder();
        sb.Append($"final=\"{ToOneLine(record.FinalMessage, 180)}\"");
        if (toolCounts.Length > 0)
        {
            sb.Append("; tools=").Append(string.Join(", ", toolCounts));
        }

        if (highlights.Length > 0)
        {
            sb.Append("; highlights=").Append(string.Join(" | ", highlights));
        }

        if (failures.Length > 0)
        {
            sb.Append("; failures=").Append(string.Join(" | ", failures));
        }

        var summary = sb.ToString();
        if (summary.Length > summaryMaxChars && summaryMaxChars > 64)
        {
            summary = summary[..summaryMaxChars] + "...";
        }

        return new MemoryRunEntry(
            record.SessionId,
            record.CompletedAtUtc,
            record.Task,
            record.Success,
            record.FinalMessage,
            summary,
            highlights,
            0.0);
    }

    private static IEnumerable<string> ExtractHighlights(IEnumerable<SessionStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.ToolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                step.ToolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) ||
                step.ToolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase))
            {
                yield return ToOneLine(step.Output, 100);
                continue;
            }

            if (step.ToolName.Equals("exec_shell", StringComparison.OrdinalIgnoreCase) ||
                step.ToolName.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
            {
                yield return ToOneLine(step.Output, 100);
                continue;
            }
        }
    }

    private static string NormalizeToken(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static string ToOneLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.Length <= maxLength)
        {
            return line;
        }

        return line[..maxLength] + "...";
    }

    private sealed record MemoryRunEntry(
        string SessionId,
        DateTimeOffset CompletedAtUtc,
        string Task,
        bool Success,
        string FinalMessage,
        string Summary,
        string[] Highlights,
        double RankScore);
}
