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

        foreach (var message in messages)
        {
            if (lines.Count > 0 && message.Role is TuiMessageRole.User or TuiMessageRole.Assistant)
            {
                lines.Add(TranscriptRenderLine.Spacer);
            }

            var time = message.CreatedAtUtc.ToLocalTime().ToString("HH:mm");
            var important = IsImportant(message);
            var prefix = BuildPrefix(time, message.Role);
            var continuation = BuildContinuation(time, message.Role);
            AppendWrapped(lines, message.Role, prefix, continuation, message.Content, width, important);
        }

        return lines;
    }

    private static string BuildPrefix(string time, TuiMessageRole role)
    {
        var marker = role switch
        {
            TuiMessageRole.User => "> ",
            TuiMessageRole.Assistant => "< ",
            TuiMessageRole.Error => "! ",
            TuiMessageRole.Status => "|- ",
            _ => "* "
        };
        return $"{time} {marker}";
    }

    private static string BuildContinuation(string time, TuiMessageRole role)
    {
        var marker = role == TuiMessageRole.Status ? "|  " : "  | ";
        return new string(' ', time.Length + 1) + marker;
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
        string continuation,
        string content,
        int maxWidth,
        bool important)
    {
        var lineWidth = Math.Max(16, maxWidth - prefix.Length);
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
