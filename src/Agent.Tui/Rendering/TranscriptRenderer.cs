using System.Text;

namespace Agent.Tui;

internal static class TranscriptRenderer
{
    public static string Render(IEnumerable<TuiMessage> messages, int maxWidth = 100)
    {
        var sb = new StringBuilder();

        foreach (var line in RenderLines(messages, maxWidth))
        {
            sb.Append(line.Text);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static IReadOnlyList<TranscriptRenderLine> RenderLines(IEnumerable<TuiMessage> messages, int maxWidth = 100)
    {
        var width = Math.Clamp(maxWidth, 40, 240);
        var lines = new List<TranscriptRenderLine>();
        var previousRole = (TuiMessageRole?)null;

        foreach (var message in messages)
        {
            var major = message.Role is not TuiMessageRole.Status;
            if (lines.Count > 0 && major && previousRole != message.Role)
            {
                lines.Add(TranscriptRenderLine.Spacer);
            }

            var time = message.CreatedAtUtc.ToLocalTime().ToString("HH:mm");
            var important = IsImportant(message);
            var label = GetLabel(message.Role);

            if (message.Role == TuiMessageRole.Status)
            {
                AppendWrapped(lines, message.Role, $"[{time}] {label}  ", message.Content, width, important);
            }
            else
            {
                lines.Add(new TranscriptRenderLine(message.Role, $"[{time}] {label}", true, important));
                AppendWrapped(lines, message.Role, "  ", message.Content, width, important);
            }

            previousRole = message.Role;
        }

        return lines;
    }

    private static string GetLabel(TuiMessageRole role)
    {
        return role switch
        {
            TuiMessageRole.User => "user",
            TuiMessageRole.Assistant => "assistant",
            TuiMessageRole.Error => "error",
            TuiMessageRole.Status => "status",
            _ => "system"
        };
    }

    private static bool IsImportant(TuiMessage message)
    {
        if (message.Role == TuiMessageRole.Error)
        {
            return true;
        }

        if (message.Role != TuiMessageRole.Status)
        {
            return false;
        }

        return message.Content.StartsWith("approval required", StringComparison.OrdinalIgnoreCase) ||
               message.Content.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
               message.Content.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               message.Content.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
               message.Content.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendWrapped(
        List<TranscriptRenderLine> lines,
        TuiMessageRole role,
        string prefix,
        string content,
        int maxWidth,
        bool important)
    {
        var lineWidth = Math.Max(16, maxWidth - prefix.Length);
        var continuation = new string(' ', prefix.Length);
        var paragraphs = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0)
            {
                lines.Add(TranscriptRenderLine.Spacer);
            }

            var currentPrefix = p == 0 ? prefix : continuation;
            var remaining = paragraphs[p];
            if (remaining.Length == 0)
            {
                lines.Add(new TranscriptRenderLine(role, currentPrefix.TrimEnd(), false, important));
                continue;
            }

            while (remaining.Length > lineWidth)
            {
                var split = FindSplit(remaining, lineWidth);
                lines.Add(new TranscriptRenderLine(role, currentPrefix + remaining[..split].TrimEnd(), false, important));
                remaining = remaining[split..].TrimStart();
                currentPrefix = continuation;
            }

            lines.Add(new TranscriptRenderLine(role, currentPrefix + remaining, false, important));
        }
    }

    private static int FindSplit(string text, int max)
    {
        for (var i = max; i > 0; i--)
        {
            if (char.IsWhiteSpace(text[i - 1]))
            {
                return i;
            }
        }

        return max;
    }
}

internal sealed record TranscriptRenderLine(
    TuiMessageRole Role,
    string Text,
    bool IsHeader,
    bool IsImportant)
{
    public static TranscriptRenderLine Spacer { get; } = new(TuiMessageRole.Status, string.Empty, false, false);
}
