using System.Text;

namespace Agent.Tui;

internal static class TranscriptRenderer
{
    public static string Render(IEnumerable<TuiMessage> messages, int maxWidth = 100)
    {
        var width = Math.Clamp(maxWidth, 40, 240);
        var sb = new StringBuilder();

        foreach (var message in messages)
        {
            var prefix = $"[{GetLabel(message.Role)}] ";
            AppendWrapped(sb, prefix, message.Content, width);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
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

    private static void AppendWrapped(StringBuilder sb, string prefix, string content, int maxWidth)
    {
        var lineWidth = Math.Max(16, maxWidth - prefix.Length);
        var continuation = new string(' ', prefix.Length);
        var paragraphs = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0)
            {
                sb.AppendLine();
            }

            var currentPrefix = p == 0 ? prefix : continuation;
            var remaining = paragraphs[p];
            if (remaining.Length == 0)
            {
                sb.Append(currentPrefix);
                continue;
            }

            while (remaining.Length > lineWidth)
            {
                var split = FindSplit(remaining, lineWidth);
                sb.Append(currentPrefix);
                sb.AppendLine(remaining[..split].TrimEnd());
                remaining = remaining[split..].TrimStart();
                currentPrefix = continuation;
            }

            sb.Append(currentPrefix);
            sb.Append(remaining);
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
