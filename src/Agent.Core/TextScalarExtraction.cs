using System.Text.RegularExpressions;

namespace Agent.Core;

internal static class TextScalarExtraction
{
    public static bool TryExtractByNamedKeys(string raw, IReadOnlyList<string> keys, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || keys.Count == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            var escaped = Regex.Escape(key);
            var quoted = Regex.Match(
                raw,
                $"(?is)[\"']{escaped}[\"']\\s*[:=]\\s*[\"'`](?<v>[^\"'`\\r\\n]{{1,6000}})[\"'`]");
            if (quoted.Success && TrySet(quoted.Groups["v"].Value, out value))
            {
                return true;
            }

            var line = Regex.Match(
                raw,
                $@"(?im)^\s*(?:[-*]\s*)?(?:[""'`])?{escaped}(?:[""'`])?\s*[:=]\s*(?<v>.+?)\s*$");
            if (line.Success && TrySet(line.Groups["v"].Value.Trim().Trim('"', '\'', '`'), out value))
            {
                return true;
            }
        }

        return false;
    }

    public static List<(string Lang, string Body)> ExtractCodeFences(string text)
    {
        var result = new List<(string Lang, string Body)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(text, "```(?<lang>[^\\r\\n`]*)\\r?\\n(?<body>[\\s\\S]*?)```"))
        {
            var lang = (match.Groups["lang"].Value ?? string.Empty).Trim();
            var body = match.Groups["body"].Value ?? string.Empty;
            result.Add((lang, body));
        }

        return result;
    }

    public static bool IsShellLanguage(string lang)
    {
        var normalized = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "bash" or "sh" or "zsh" or "shell" or "console" or "cmd" or "bat" or "powershell" or "pwsh" or "ps1";
    }

    public static bool IsDiffLanguage(string lang)
    {
        var normalized = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "diff" or "patch";
    }

    public static string NormalizeShellCommandBlock(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                if (line.StartsWith("$ ", StringComparison.Ordinal))
                {
                    return line[2..].Trim();
                }

                if (line.StartsWith("PS> ", StringComparison.OrdinalIgnoreCase))
                {
                    return line[4..].Trim();
                }

                return line;
            })
            .ToList();

        return string.Join('\n', lines);
    }

    private static bool TrySet(string raw, out string value)
    {
        value = raw.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}
