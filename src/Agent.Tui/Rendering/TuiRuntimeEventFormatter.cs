using Agent.Core;

namespace Agent.Tui;

internal static class TuiRuntimeEventFormatter
{
    public static TuiMessage Format(AgentRunEvent evt)
    {
        var text = FormatText(evt);
        return evt.Type switch
        {
            AgentRunEventType.Error => TuiMessage.Error(text),
            AgentRunEventType.PolicyDenied => TuiMessage.Error(text),
            AgentRunEventType.ApprovalRejected => TuiMessage.Error(text),
            AgentRunEventType.SessionCompleted => TuiMessage.Assistant(text),
            _ => TuiMessage.Status(text)
        };
    }

    public static string FormatText(AgentRunEvent evt)
    {
        var step = evt.Step.HasValue ? $"#{evt.Step.Value} " : string.Empty;
        var tool = string.IsNullOrWhiteSpace(evt.ToolName) ? string.Empty : $" {evt.ToolName}";

        return evt.Type switch
        {
            AgentRunEventType.SessionStarted => $"session: {evt.Message}",
            AgentRunEventType.MemoryLoaded => $"memory: {evt.Message}",
            AgentRunEventType.ContextCompacted => $"{step}memory: {evt.Message}",
            AgentRunEventType.ModelCallStarted => $"{step}model: {evt.Message}",
            AgentRunEventType.ModelCallCompleted => $"{step}model: response received",
            AgentRunEventType.ModelProfileSwitched => $"{step}model: {evt.Message}",
            AgentRunEventType.ModelDecisionRecovered => $"{step}recover: {evt.Message}",
            AgentRunEventType.ModelResponseInvalid => $"{step}warn: {evt.Message}",
            AgentRunEventType.FinalRejectedRequiresTool => $"{step}warn: {evt.Message}",
            AgentRunEventType.ToolDecision => $"{step}plan:{tool} - {evt.Message}",
            AgentRunEventType.PolicyDenied => $"{step}denied:{tool} - {evt.Message}",
            AgentRunEventType.ApprovalRequired => $"{step}approval required:{tool} - {evt.Message}",
            AgentRunEventType.ApprovalGranted => $"{step}approved:{tool}",
            AgentRunEventType.ApprovalRejected => $"{step}rejected:{tool}",
            AgentRunEventType.ToolExecutionStarted => $"{step}run:{tool}",
            AgentRunEventType.ToolExecutionCompleted => $"{step}{FormatToolCompletion(evt)}",
            AgentRunEventType.MemoryUpdated => $"memory: {evt.Message}",
            AgentRunEventType.SessionCompleted => $"done: {evt.Message}",
            AgentRunEventType.Error => $"error: {evt.Message}",
            _ => $"{step}{evt.Type}: {evt.Message}"
        };
    }

    private static string FormatToolCompletion(AgentRunEvent evt)
    {
        var tool = string.IsNullOrWhiteSpace(evt.ToolName) ? "tool" : evt.ToolName;
        var success = IsSuccess(evt);
        var summary = TryGetMetadata(evt.Metadata, ToolActivityMetadata.SummaryKey);

        if (!success)
        {
            return $"failed: {tool} - {ToOneLine(summary ?? evt.Message, 180)}";
        }

        var kind = TryGetMetadata(evt.Metadata, ToolActivityMetadata.KindKey);
        return kind switch
        {
            "read" => FormatSubject("read", TryGetMetadata(evt.Metadata, ToolActivityMetadata.PathKey), tool, summary, evt.Message),
            "edit" => FormatSubject("edit", TryGetMetadata(evt.Metadata, ToolActivityMetadata.PathKey), tool, summary, evt.Message),
            "explore" => FormatSubject("list", TryGetMetadata(evt.Metadata, ToolActivityMetadata.PathKey), tool, summary, evt.Message),
            "search" => FormatSubject("search", Quote(TryGetMetadata(evt.Metadata, ToolActivityMetadata.QueryKey)), tool, summary, evt.Message),
            "command" => FormatSubject("command", TryGetMetadata(evt.Metadata, ToolActivityMetadata.CommandKey), tool, summary, evt.Message),
            _ => $"ok: {tool} - {ToOneLine(summary ?? evt.Message, 180)}"
        };
    }

    private static string FormatSubject(string label, string? subject, string tool, string? summary, string message)
    {
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return $"{label}: {ToOneLine(subject, 160)} ({tool})";
        }

        return $"{label}: {tool} - {ToOneLine(summary ?? message, 180)}";
    }

    private static bool IsSuccess(AgentRunEvent evt)
    {
        if (evt.Metadata is not null &&
            evt.Metadata.TryGetValue(ToolActivityMetadata.SuccessKey, out var value) &&
            bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return !evt.Message.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetMetadata(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata is not null && metadata.TryGetValue(key, out var value) ? value : null;

    private static string? Quote(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : $"\"{value}\"";

    private static string ToOneLine(string value, int maxLength)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "...";
    }
}
