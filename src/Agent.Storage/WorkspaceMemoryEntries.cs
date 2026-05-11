using System.Security.Cryptography;
using System.Text;
using Agent.Core;

namespace Agent.Storage;

public sealed partial class WorkspaceMemoryStore
{
    private static IEnumerable<MemoryRunEntry> RankEntries(IEnumerable<MemoryRunEntry> entries, string task)
    {
        var orderedByRecency = entries
            .OrderByDescending(e => e.CompletedAtUtc)
            .ToList();

        for (var i = 0; i < orderedByRecency.Count; i++)
        {
            var entry = orderedByRecency[i];
            var overlap = ScoreTaskOverlap(task, $"{entry.Task} {entry.Summary} {string.Join(' ', entry.Highlights)}");
            var recencyBoost = 1.0 / (1 + i);
            var outcomeAdjustment = entry.Success ? 0.20 : -0.35;
            var actionBoost = entry.Highlights.Length > 0 ? 0.15 : -0.10;
            entry = entry with { RankScore = (overlap * 2.2) + (recencyBoost * 0.35) + outcomeAdjustment + actionBoost };
            orderedByRecency[i] = entry;
        }

        return orderedByRecency
            .OrderByDescending(e => e.RankScore)
            .ThenByDescending(e => e.CompletedAtUtc);
    }

    private static bool ShouldIncludeInContext(MemoryRunEntry entry, string task)
    {
        var overlap = ScoreTaskOverlap(task, $"{entry.Task} {entry.Summary} {string.Join(' ', entry.Highlights)}");
        if (entry.Success)
        {
            return overlap >= 0.18 || entry.RankScore >= 0.95;
        }

        return overlap >= 0.45 && entry.Highlights.Length > 0;
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
        if (highlights.Length > 0)
        {
            sb.Append("highlights=").Append(string.Join(" | ", highlights));
        }

        if (toolCounts.Length > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append("; ");
            }

            sb.Append("tools=").Append(string.Join(", ", toolCounts));
        }

        if (failures.Length > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append("; ");
            }

            sb.Append("failures=").Append(string.Join(" | ", failures));
        }

        if (!string.IsNullOrWhiteSpace(record.FinalMessage))
        {
            if (sb.Length > 0)
            {
                sb.Append("; ");
            }

            sb.Append("final=\"").Append(ToOneLine(record.FinalMessage, 180)).Append('"');
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

            if (step.ToolName.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
            {
                yield return ToOneLine(step.Output, 100);
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

}
