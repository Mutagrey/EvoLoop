using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Tui;

internal sealed record TuiStorageCommandResult(bool Success, string Message);

internal static class TuiStorageInspector
{
    private static readonly string[] RotatedJsonlFiles =
    {
        "sessions.jsonl",
        "steps.jsonl",
        "events.jsonl"
    };

    public static string FormatStorage(string workspaceRoot)
    {
        var storageRoot = StorageRoot(workspaceRoot);
        var sb = new StringBuilder();
        sb.AppendLine("Storage");
        sb.AppendLine($"|- root: {storageRoot}");

        if (!Directory.Exists(storageRoot))
        {
            sb.AppendLine("`- status: storage directory does not exist");
            return sb.ToString().TrimEnd();
        }

        var files = new[]
        {
            "sessions.jsonl",
            "steps.jsonl",
            "events.jsonl",
            "memory-runs.jsonl",
            "agent.db"
        };

        foreach (var file in files)
        {
            var path = Path.Combine(storageRoot, file);
            sb.AppendLine($"|- {file}: {FormatFile(path)}");
        }

        var snapshots = Path.Combine(storageRoot, "snapshots");
        var snapshotFiles = Directory.Exists(snapshots)
            ? Directory.EnumerateFiles(snapshots, "*", SearchOption.AllDirectories).ToArray()
            : Array.Empty<string>();
        sb.AppendLine($"`- snapshots: files={snapshotFiles.Length}; size={FormatBytes(snapshotFiles.Sum(SafeLength))}");
        return sb.ToString().TrimEnd();
    }

    public static string FormatSessions(string workspaceRoot, int maxSessions = 20)
    {
        var sessions = LoadSessionSummaries(workspaceRoot)
            .OrderByDescending(s => s.StartedAtUtc ?? DateTimeOffset.MinValue)
            .Take(Math.Clamp(maxSessions, 1, 100))
            .ToArray();

        if (sessions.Length == 0)
        {
            return "No sessions found.";
        }

        var stepCounts = LoadStepCounts(workspaceRoot);
        var sb = new StringBuilder();
        sb.AppendLine($"Sessions (latest {sessions.Length})");
        for (var i = 0; i < sessions.Length; i++)
        {
            var marker = i == sessions.Length - 1 ? "`-" : "|-";
            var session = sessions[i];
            stepCounts.TryGetValue(session.SessionId, out var steps);
            sb.AppendLine(
                $"{marker} {ShortId(session.SessionId)} status={session.Status}; steps={steps}; profile={ValueOrNone(session.Profile)}; started={FormatTime(session.StartedAtUtc)}; task=\"{Clip(session.Task, 90)}\"");
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatSession(string workspaceRoot, string query)
    {
        var sessions = LoadSessionSummaries(workspaceRoot);
        var matches = sessions
            .Where(s => s.SessionId.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.StartedAtUtc ?? DateTimeOffset.MinValue)
            .ToArray();

        if (matches.Length == 0)
        {
            return $"Session not found: {query}";
        }

        if (matches.Length > 1)
        {
            return $"Session id is ambiguous: {query}. Matches: {string.Join(", ", matches.Take(8).Select(s => ShortId(s.SessionId)))}";
        }

        var session = matches[0];
        var steps = LoadSteps(workspaceRoot, session.SessionId).ToArray();
        var events = LoadEvents(workspaceRoot, session.SessionId).ToArray();
        var final = events.LastOrDefault(e => e.EventType.Equals(AgentEventTypes.FinalAnswer, StringComparison.OrdinalIgnoreCase))?.Message;

        var sb = new StringBuilder();
        sb.AppendLine("Session");
        sb.AppendLine($"|- id: {session.SessionId}");
        sb.AppendLine($"|- status: {session.Status}");
        sb.AppendLine($"|- profile: {ValueOrNone(session.Profile)}");
        sb.AppendLine($"|- started: {FormatTime(session.StartedAtUtc)}");
        sb.AppendLine($"|- completed: {FormatTime(session.CompletedAtUtc)}");
        sb.AppendLine($"|- task: {Clip(session.Task, 220)}");
        if (!string.IsNullOrWhiteSpace(final))
        {
            sb.AppendLine($"|- final: {Clip(final, 220)}");
        }

        sb.AppendLine("|- steps");
        if (steps.Length == 0)
        {
            sb.AppendLine("|  `- <none>");
        }
        else
        {
            foreach (var step in steps.TakeLast(30))
            {
                sb.AppendLine($"|  |- #{step.StepNumber} {step.ToolName} success={step.Success} {step.DurationMs}ms {Clip(step.Error ?? step.Output, 120)}");
            }
        }

        sb.AppendLine("`- events");
        if (events.Length == 0)
        {
            sb.AppendLine("   `- <none>");
        }
        else
        {
            foreach (var evt in events.TakeLast(40))
            {
                sb.AppendLine($"   |- {evt.EventType}: {Clip(evt.Message, 140)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatMemory(string workspaceRoot, int maxEntries = 12)
    {
        var path = Path.Combine(StorageRoot(workspaceRoot), "memory-runs.jsonl");
        if (!File.Exists(path))
        {
            return "No workspace memory available yet.";
        }

        var entries = ReadJsonLines(path)
            .Select(ParseMemory)
            .Where(e => e is not null)
            .Cast<MemorySummary>()
            .OrderByDescending(e => e.CompletedAtUtc)
            .Take(Math.Clamp(maxEntries, 1, 50))
            .ToArray();

        if (entries.Length == 0)
        {
            return "No readable workspace memory entries found.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Workspace Memory (latest {entries.Length})");
        for (var i = 0; i < entries.Length; i++)
        {
            var marker = i == entries.Length - 1 ? "`-" : "|-";
            var entry = entries[i];
            sb.AppendLine($"{marker} {FormatTime(entry.CompletedAtUtc)} success={entry.Success}; task=\"{Clip(entry.Task, 80)}\"; {Clip(entry.Summary, 180)}");
        }

        return sb.ToString().TrimEnd();
    }

    public static TuiStorageCommandResult Archive(string workspaceRoot)
    {
        var storageRoot = StorageRoot(workspaceRoot);
        if (!Directory.Exists(storageRoot))
        {
            return new TuiStorageCommandResult(false, "Storage directory does not exist.");
        }

        var archiveDir = Path.Combine(storageRoot, "archive", "archive-" + Timestamp());
        Directory.CreateDirectory(archiveDir);
        var moved = 0;
        foreach (var fileName in RotatedJsonlFiles)
        {
            var path = Path.Combine(storageRoot, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            File.Move(path, Path.Combine(archiveDir, fileName));
            File.WriteAllText(path, string.Empty, Encoding.UTF8);
            moved++;
        }

        return moved == 0
            ? new TuiStorageCommandResult(false, "No session/event JSONL files were available to archive.")
            : new TuiStorageCommandResult(true, $"Archived {moved} JSONL files to {archiveDir}.");
    }

    public static TuiStorageCommandResult Prune(string workspaceRoot, int keepSessions)
    {
        if (keepSessions < 1)
        {
            return new TuiStorageCommandResult(false, "Keep count must be at least 1.");
        }

        var storageRoot = StorageRoot(workspaceRoot);
        if (!Directory.Exists(storageRoot))
        {
            return new TuiStorageCommandResult(false, "Storage directory does not exist.");
        }

        var sessions = LoadSessionSummaries(workspaceRoot)
            .OrderByDescending(s => s.StartedAtUtc ?? DateTimeOffset.MinValue)
            .ToArray();
        var keepIds = sessions
            .Take(keepSessions)
            .Select(s => s.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keepIds.Count == 0)
        {
            return new TuiStorageCommandResult(false, "No sessions found to keep.");
        }

        var archiveDir = Path.Combine(storageRoot, "archive", "prune-" + Timestamp());
        Directory.CreateDirectory(archiveDir);
        foreach (var fileName in RotatedJsonlFiles)
        {
            var path = Path.Combine(storageRoot, fileName);
            if (File.Exists(path))
            {
                File.Copy(path, Path.Combine(archiveDir, fileName), overwrite: true);
            }
        }

        FilterJsonl(Path.Combine(storageRoot, "sessions.jsonl"), keepIds, TryGetSessionId);
        FilterJsonl(Path.Combine(storageRoot, "steps.jsonl"), keepIds, TryGetSessionId);
        FilterJsonl(Path.Combine(storageRoot, "events.jsonl"), keepIds, TryGetSessionId);

        return new TuiStorageCommandResult(
            true,
            $"Pruned session/event JSONL to latest {keepIds.Count} sessions. Archive copy: {archiveDir}.");
    }

    public static TuiStorageCommandResult Compact(
        TuiRuntimeInfo runtime,
        IReadOnlyList<TuiMessage> messages,
        AgentRunResult? lastRun,
        AgentRunResult? lastPlan)
    {
        var storageRoot = StorageRoot(runtime.Workspace);
        Directory.CreateDirectory(storageRoot);
        var sessionId = lastRun?.SessionId ?? lastPlan?.SessionId ?? "compact-" + Guid.NewGuid().ToString("n");
        var summary = BuildContextSummary(runtime, messages, lastRun, lastPlan);
        var now = DateTimeOffset.UtcNow;

        var evt = new AgentEventRecord(
            sessionId,
            AgentEventTypes.ContextSummary,
            now,
            summary,
            null,
            true,
            new Dictionary<string, string>
            {
                ["source"] = "tui_manual",
                ["profile"] = runtime.Profile,
                ["message_count"] = messages.Count.ToString()
            });
        AppendJsonLine(Path.Combine(storageRoot, "events.jsonl"), evt);

        if (runtime.MemoryEnabled)
        {
            var memoryEntry = new
            {
                SessionId = sessionId,
                CompletedAtUtc = now,
                Task = "Manual TUI context compaction",
                Success = true,
                FinalMessage = summary,
                Summary = Clip(summary, runtime.MemoryContextMaxChars),
                Highlights = BuildHighlights(messages),
                RankScore = 0.0,
                ProjectId = string.Empty,
                WorkspaceRoot = runtime.Workspace,
                WorkspaceRootHash = string.Empty
            };
            AppendJsonLine(Path.Combine(storageRoot, "memory-runs.jsonl"), memoryEntry);
        }

        return new TuiStorageCommandResult(
            true,
            runtime.MemoryEnabled
                ? $"Compacted visible context into events and workspace memory for session {ShortId(sessionId)}."
                : $"Compacted visible context into events for session {ShortId(sessionId)}. Memory is disabled.");
    }

    private static string BuildContextSummary(
        TuiRuntimeInfo runtime,
        IReadOnlyList<TuiMessage> messages,
        AgentRunResult? lastRun,
        AgentRunResult? lastPlan)
    {
        var maxChars = Math.Max(1200, runtime.ContextHistorySummaryChars);
        var sb = new StringBuilder();
        sb.AppendLine("MANUAL TUI CONTEXT SUMMARY");
        sb.AppendLine($"profile: {runtime.Profile}; mode: {runtime.ModeLabel}; workspace: {runtime.Workspace}");
        if (lastRun is not null)
        {
            sb.AppendLine($"last_run: session={lastRun.SessionId}; success={lastRun.Success}; steps={lastRun.Steps}; final={Clip(lastRun.FinalMessage, 220)}");
        }

        if (lastPlan is not null)
        {
            sb.AppendLine($"last_plan: session={lastPlan.SessionId}; success={lastPlan.Success}; steps={lastPlan.Steps}; final={Clip(lastPlan.FinalMessage, 220)}");
        }

        sb.AppendLine("recent_transcript:");
        foreach (var message in messages
                     .Where(m => !m.Content.Equals("/compact", StringComparison.OrdinalIgnoreCase))
                     .TakeLast(24))
        {
            sb.Append("- ").Append(message.Role.ToString().ToLowerInvariant()).Append(": ");
            sb.AppendLine(Clip(message.Content, 220));
        }

        return Clip(sb.ToString().TrimEnd(), maxChars);
    }

    private static string[] BuildHighlights(IReadOnlyList<TuiMessage> messages)
    {
        return messages
            .Where(m => m.Role is TuiMessageRole.User or TuiMessageRole.Assistant or TuiMessageRole.Error)
            .Select(m => $"{m.Role}: {Clip(m.Content, 120)}")
            .TakeLast(8)
            .ToArray();
    }

    private static void FilterJsonl(string path, HashSet<string> keepIds, Func<JsonElement, string?> idSelector)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var kept = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var id = idSelector(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(id) && keepIds.Contains(id))
                {
                    kept.Add(line);
                }
            }
            catch
            {
                // Malformed lines stay recoverable from the archive copy created before pruning.
            }
        }

        File.WriteAllLines(path, kept, Encoding.UTF8);
    }

    private static IReadOnlyList<SessionSummary> LoadSessionSummaries(string workspaceRoot)
    {
        var sessionsPath = Path.Combine(StorageRoot(workspaceRoot), "sessions.jsonl");
        var byId = new Dictionary<string, SessionSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadJsonLines(sessionsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = GetString(root, "type", "EventType") ?? string.Empty;
                var id = TryGetSessionId(root);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                byId.TryGetValue(id, out var current);
                current ??= new SessionSummary(id, null, null, null, null, "running", string.Empty);
                if (type.Equals(AgentEventTypes.SessionStart, StringComparison.OrdinalIgnoreCase))
                {
                    current = current with
                    {
                        StartedAtUtc = GetDate(root, "startedAtUtc", "StartedAtUtc") ?? current.StartedAtUtc,
                        WorkspaceRoot = GetString(root, "workspaceRoot", "WorkspaceRoot") ?? current.WorkspaceRoot,
                        Profile = GetString(root, "profile", "Profile") ?? current.Profile,
                        Task = GetString(root, "task", "Task") ?? current.Task,
                        Status = current.Status
                    };
                }
                else if (type.Equals(AgentEventTypes.SessionEnd, StringComparison.OrdinalIgnoreCase))
                {
                    current = current with
                    {
                        CompletedAtUtc = GetDate(root, "completedAtUtc", "CompletedAtUtc") ?? current.CompletedAtUtc,
                        Status = GetString(root, "finalStatus", "FinalStatus") ?? GetString(root, "Message") ?? current.Status
                    };
                }

                byId[id] = current;
            }
            catch
            {
                // Ignore malformed session lines.
            }
        }

        return byId.Values.ToArray();
    }

    private static IReadOnlyDictionary<string, int> LoadStepCounts(string workspaceRoot)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in LoadSteps(workspaceRoot, null))
        {
            counts.TryGetValue(step.SessionId, out var count);
            counts[step.SessionId] = count + 1;
        }

        return counts;
    }

    private static IEnumerable<StepSummary> LoadSteps(string workspaceRoot, string? sessionId)
    {
        var path = Path.Combine(StorageRoot(workspaceRoot), "steps.jsonl");
        foreach (var line in ReadJsonLines(path))
        {
            StepSummary? step = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = TryGetSessionId(root);
                if (string.IsNullOrWhiteSpace(id) ||
                    (!string.IsNullOrWhiteSpace(sessionId) && !id.Equals(sessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                step = new StepSummary(
                    id,
                    GetInt(root, "StepNumber", "stepNumber"),
                    GetString(root, "ToolName", "toolName") ?? "<unknown>",
                    GetBool(root, "Success", "success"),
                    GetString(root, "Output", "output") ?? string.Empty,
                    GetString(root, "Error", "error"),
                    GetLong(root, "DurationMs", "durationMs"));
            }
            catch
            {
                // Ignore malformed step lines.
            }

            if (step is not null)
            {
                yield return step;
            }
        }
    }

    private static IEnumerable<EventSummary> LoadEvents(string workspaceRoot, string sessionId)
    {
        var path = Path.Combine(StorageRoot(workspaceRoot), "events.jsonl");
        foreach (var line in ReadJsonLines(path))
        {
            EventSummary? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = TryGetSessionId(root);
                if (string.IsNullOrWhiteSpace(id) || !id.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                evt = new EventSummary(
                    id,
                    GetString(root, "EventType", "eventType") ?? "<unknown>",
                    GetString(root, "Message", "message") ?? string.Empty);
            }
            catch
            {
                // Ignore malformed event lines.
            }

            if (evt is not null)
            {
                yield return evt;
            }
        }
    }

    private static MemorySummary? ParseMemory(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return new MemorySummary(
                GetDate(root, "CompletedAtUtc", "completedAtUtc") ?? DateTimeOffset.MinValue,
                GetBool(root, "Success", "success"),
                GetString(root, "Task", "task") ?? string.Empty,
                GetString(root, "Summary", "summary") ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadJsonLines(string path)
    {
        return File.Exists(path)
            ? File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
            : Enumerable.Empty<string>();
    }

    private static void AppendJsonLine(string path, object payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.AppendAllText(path, JsonSerializer.Serialize(payload) + Environment.NewLine, Encoding.UTF8);
    }

    private static string? TryGetSessionId(JsonElement root)
        => GetString(root, "sessionId", "SessionId");

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
        }

        return null;
    }

    private static int GetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return 0;
    }

    private static long GetLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result))
            {
                return result;
            }
        }

        return 0;
    }

    private static bool GetBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.True ||
                       (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0);
            }
        }

        return false;
    }

    private static DateTimeOffset? GetDate(JsonElement root, params string[] names)
    {
        var raw = GetString(root, names);
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string FormatFile(string path)
    {
        if (!File.Exists(path))
        {
            return "<missing>";
        }

        var info = new FileInfo(path);
        var lineCount = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? File.ReadLines(path).Count()
            : 0;
        return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? $"{FormatBytes(info.Length)}; lines={lineCount}"
            : FormatBytes(info.Length);
    }

    private static string StorageRoot(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".evoloop", "storage");

    private static string FormatTime(DateTimeOffset? value)
        => value is null || value == DateTimeOffset.MinValue ? "<unknown>" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{size:0.0} {units[unit]}";
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string ShortId(string sessionId)
        => sessionId.Length <= 12 ? sessionId : sessionId[..12];

    private static string ValueOrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<none>" : value;

    private static string Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= maxLength ? line : line[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string Timestamp()
        => DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

    private sealed record SessionSummary(
        string SessionId,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? WorkspaceRoot,
        string? Profile,
        string Status,
        string Task);

    private sealed record StepSummary(
        string SessionId,
        int StepNumber,
        string ToolName,
        bool Success,
        string Output,
        string? Error,
        long DurationMs);

    private sealed record EventSummary(
        string SessionId,
        string EventType,
        string Message);

    private sealed record MemorySummary(
        DateTimeOffset CompletedAtUtc,
        bool Success,
        string Task,
        string Summary);
}
