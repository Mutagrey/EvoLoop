using System.Text;

namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private static string BuildObservationMessage(string toolName, ToolResult result, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OBSERVATION from {toolName}");
        sb.AppendLine($"success: {result.Success}");
        sb.AppendLine($"message: {ToOneLine(result.Message, 400)}");

        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine("stdout:");
            sb.AppendLine(ClipToChars(result.StdOut, Math.Max(200, maxChars / 2)));
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            sb.AppendLine("stderr:");
            sb.AppendLine(ClipToChars(result.StdErr, Math.Max(120, maxChars / 3)));
        }

        return ClipToChars(sb.ToString(), Math.Max(300, maxChars));
    }

    private static string BuildToolPlanMessage(string toolName, string reason)
    {
        var headline = toolName switch
        {
            "fs_list" => "Inspect workspace structure",
            "fs_read" => "Read target file",
            "fs_write" => "Write/update file",
            "fs_patch" => "Apply patch to file",
            "fs_delete" => "Delete file or directory",
            "search_lexical" => "Find matching code by text search",
            "search_semantic" => "Find relevant code by reranked search",
            "exec_shell" => "Run shell command",
            "git_status" => "Check git status",
            "git_diff" => "Inspect git diff",
            "git_log" => "Inspect recent commits",
            "git_show" => "Inspect commit/object",
            "git_add" => "Stage changes",
            "git_commit" => "Create commit",
            _ => $"Use tool {toolName}"
        };

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : ToOneLine(reason, 120);
        var reasonProbe = normalizedReason.TrimStart();
        if (reasonProbe.StartsWith("{", StringComparison.Ordinal) ||
            reasonProbe.StartsWith("[", StringComparison.Ordinal) ||
            reasonProbe.StartsWith("```", StringComparison.Ordinal))
        {
            normalizedReason = string.Empty;
        }

        return string.IsNullOrWhiteSpace(normalizedReason)
            ? headline
            : $"{headline}. {normalizedReason}";
    }

    private static string ToOneLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= maxLength)
        {
            return oneLine;
        }

        return oneLine[..maxLength] + "...";
    }

    private static string ClipToChars(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        if (maxChars <= 3)
        {
            return value[..Math.Max(0, maxChars)];
        }

        return value[..(maxChars - 3)] + "...";
    }

    private static string BuildAdaptiveDirective(
        bool requiresToolBeforeFinal,
        int toolStepsExecuted,
        int consecutiveInvalidResponses,
        int consecutiveFinalWithoutTools,
        string lastModelIssue)
    {
        if (consecutiveInvalidResponses <= 0 &&
            consecutiveFinalWithoutTools <= 0 &&
            string.IsNullOrWhiteSpace(lastModelIssue))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Adapt output strategy now.");
        sb.AppendLine("Return exactly one JSON object and nothing else.");
        sb.AppendLine("Allowed schemas:");
        sb.AppendLine("1) {\"type\":\"tool\",\"tool\":\"...\",\"reason\":\"...\",\"arguments\":{...}}");
        sb.AppendLine("2) {\"type\":\"final\",\"message\":\"...\"}");
        sb.AppendLine("3) {\"type\":\"clarify\",\"message\":\"...\"}");

        if (requiresToolBeforeFinal && toolStepsExecuted == 0)
        {
            sb.AppendLine("Do NOT return final yet. Call one appropriate tool first.");
        }

        if (consecutiveInvalidResponses > 0)
        {
            sb.AppendLine($"Recent format failures: {consecutiveInvalidResponses}. Keep JSON compact and valid.");
            sb.AppendLine("Do not include markdown/code fences/comments.");
        }

        if (consecutiveFinalWithoutTools > 0)
        {
            sb.AppendLine($"Recent premature final replies: {consecutiveFinalWithoutTools}. Execute a tool first.");
        }

        if (!string.IsNullOrWhiteSpace(lastModelIssue))
        {
            sb.AppendLine($"Last detected issue: {ToOneLine(lastModelIssue, 120)}.");
            if (lastModelIssue.StartsWith("missing_required_arguments:", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("When calling a tool, every required argument must be present and non-empty.");
                sb.AppendLine("If path is unknown, call fs_list or search_lexical first, then retry with concrete path.");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryCompactHistory(
        List<ModelMessage> history,
        RuntimeConfig runtime,
        out string summary)
    {
        summary = string.Empty;
        var maxMessages = Math.Max(20, runtime.HistoryMaxMessages);
        var maxChars = Math.Max(12000, runtime.HistoryMaxChars);
        if (history.Count <= maxMessages && EstimateHistoryChars(history) <= maxChars)
        {
            return false;
        }

        var keepHead = Math.Min(history.Count, 2);
        var keepTail = Math.Clamp(runtime.HistoryKeepTailMessages, 8, Math.Max(8, maxMessages - keepHead - 1));
        var tailStart = Math.Max(keepHead, history.Count - keepTail);
        if (tailStart <= keepHead)
        {
            return false;
        }

        var middle = history.Skip(keepHead).Take(tailStart - keepHead).ToList();
        if (middle.Count == 0)
        {
            return false;
        }

        var compacted = BuildCompactedHistoryMessage(middle, Math.Max(1200, maxChars / 3));
        var oldCount = history.Count;
        history.RemoveRange(keepHead, tailStart - keepHead);
        history.Insert(keepHead, new ModelMessage("user", compacted));
        summary = $"history messages {oldCount} -> {history.Count}";
        return true;
    }

    private static int EstimateHistoryChars(IEnumerable<ModelMessage> history)
    {
        var total = 0;
        foreach (var message in history)
        {
            total += message.Content?.Length ?? 0;
        }

        return total;
    }

    private static string BuildCompactedHistoryMessage(IReadOnlyList<ModelMessage> middle, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine("COMPACTED CONTEXT SUMMARY (older exchanges):");

        var emitted = 0;
        foreach (var message in middle)
        {
            var content = message.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (content.StartsWith("COMPACTED CONTEXT SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = SummarizeMessageForCompaction(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            sb.Append("- ").AppendLine(normalized);
            emitted++;
            if (emitted >= 20)
            {
                break;
            }
        }

        if (emitted == 0)
        {
            sb.AppendLine("- Previous conversation details were compacted.");
        }

        sb.AppendLine("Use this summary as context. Prefer current tool observations over older details.");
        return ClipToChars(sb.ToString().TrimEnd(), maxChars);
    }

    private static string SummarizeMessageForCompaction(ModelMessage message)
    {
        var content = message.Content ?? string.Empty;
        if (content.StartsWith("OBSERVATION from ", StringComparison.OrdinalIgnoreCase))
        {
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var tool = lines.Length > 0 ? lines[0].Replace("OBSERVATION from ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim() : "tool";
            var success = lines.FirstOrDefault(line => line.StartsWith("success:", StringComparison.OrdinalIgnoreCase)) ?? "success: ?";
            var msg = lines.FirstOrDefault(line => line.StartsWith("message:", StringComparison.OrdinalIgnoreCase)) ?? "message: <none>";
            return $"{tool} | {ToOneLine(success, 48)} | {ToOneLine(msg, 160)}";
        }

        if (content.StartsWith("OBSERVATION:", StringComparison.OrdinalIgnoreCase))
        {
            return ToOneLine(content["OBSERVATION:".Length..].Trim(), 220);
        }

        if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
            content.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return ToOneLine(content, 220);
    }

    private static bool TryRecoverPlainFinalMessage(string content, out string recovered)
    {
        recovered = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        if (trimmed.Length < 12)
        {
            return false;
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("\"type\"") || lower.Contains("```") || lower.Contains("tool") || lower.Contains("action:"))
        {
            return false;
        }

        recovered = ClipToChars(trimmed, 2000);
        return true;
    }

}
