using System.Security.Cryptography;
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

    private static MemoryRunEntry BuildEntry(
        WorkspaceMemoryRecord record,
        int summaryMaxChars,
        string projectId,
        string workspaceRoot,
        string workspaceRootHash)
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
            0.0,
            projectId,
            workspaceRoot,
            workspaceRootHash);
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

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }

    private static string ComputeSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string ResolveOrCreateProjectIdentity(string workspaceRoot)
    {
        var identityPath = Path.Combine(workspaceRoot, ".evoloop", "project.identity.json");
        var identityDir = Path.GetDirectoryName(identityPath);
        if (!string.IsNullOrWhiteSpace(identityDir))
        {
            Directory.CreateDirectory(identityDir);
        }

        ProjectIdentityDocument? existing = null;
        if (File.Exists(identityPath))
        {
            try
            {
                existing = JsonSerializer.Deserialize<ProjectIdentityDocument>(File.ReadAllText(identityPath), JsonOptions);
            }
            catch
            {
                existing = null;
            }
        }

        var projectId = existing?.ProjectId?.Trim();
        var createdAtUtc = existing?.CreatedAtUtc;
        var firstWorkspaceRoot = existing?.FirstWorkspaceRoot;
        var derivation = existing?.Derivation;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            var origin = TryReadGitOriginUrl(workspaceRoot);
            if (!string.IsNullOrWhiteSpace(origin))
            {
                projectId = "git-" + ComputeSha256Hex(origin.Trim().ToLowerInvariant())[..24];
                derivation = "git_origin";
            }
            else
            {
                projectId = "local-" + Guid.NewGuid().ToString("n");
                derivation = "local_guid";
            }
        }

        createdAtUtc ??= DateTimeOffset.UtcNow.ToString("O");
        firstWorkspaceRoot ??= workspaceRoot;
        derivation ??= "local_guid";

        var normalizedCurrentRoot = Path.GetFullPath(workspaceRoot);
        var needsSave = existing is null ||
                        !string.Equals(existing.ProjectId, projectId, StringComparison.Ordinal) ||
                        !string.Equals(existing.LastWorkspaceRoot, normalizedCurrentRoot, StringComparison.Ordinal) ||
                        !string.Equals(existing.FirstWorkspaceRoot, firstWorkspaceRoot, StringComparison.Ordinal) ||
                        !string.Equals(existing.Derivation, derivation, StringComparison.Ordinal);

        if (needsSave)
        {
            var doc = new ProjectIdentityDocument(
                projectId,
                createdAtUtc,
                firstWorkspaceRoot,
                normalizedCurrentRoot,
                derivation);
            try
            {
                File.WriteAllText(identityPath, JsonSerializer.Serialize(doc, JsonOptions), Encoding.UTF8);
            }
            catch
            {
                // Identity persistence is best-effort.
            }
        }

        return projectId;
    }

    private static string? TryResolvePortableRunsPath(string projectId)
    {
        var dataRoot = GetUserDataRoot();
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return null;
        }

        try
        {
            var memoryRoot = Path.Combine(dataRoot, "memory");
            Directory.CreateDirectory(memoryRoot);
            return Path.Combine(memoryRoot, $"{projectId}.jsonl");
        }
        catch
        {
            return null;
        }
    }

    private static string? GetUserDataRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".evoloop");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "EvoLoop");
        }

        return null;
    }

    private static string? TryReadGitOriginUrl(string workspaceRoot)
    {
        var configPath = TryResolveGitConfigPath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(configPath);
            return ParseGitOriginUrl(lines);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveGitConfigPath(string workspaceRoot)
    {
        var dotGit = Path.Combine(workspaceRoot, ".git");
        if (Directory.Exists(dotGit))
        {
            return Path.Combine(dotGit, "config");
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        try
        {
            var line = File.ReadLines(dotGit).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var gitDirRaw = trimmed["gitdir:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(gitDirRaw))
            {
                return null;
            }

            var gitDir = Path.IsPathRooted(gitDirRaw)
                ? gitDirRaw
                : Path.GetFullPath(Path.Combine(workspaceRoot, gitDirRaw));
            return Path.Combine(gitDir, "config");
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseGitOriginUrl(IEnumerable<string> lines)
    {
        var inOrigin = false;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin)
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            if (!key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(equalsIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
