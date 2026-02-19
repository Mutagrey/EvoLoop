using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core;

public static class ToolArgumentReader
{
    private static readonly string[] NestedContainerKeys =
    {
        "arguments",
        "params",
        "payload",
        "tool_input",
        "action_input",
        "data",
        "input"
    };

    private static readonly Dictionary<string, string[]> AliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["path"] = new[] { "file", "file_path", "filepath", "filename", "target", "target_path", "relative_path", "name", "location", "path_name" },
        ["query"] = new[] { "text", "pattern", "keyword", "search", "term", "needle" },
        ["command"] = new[] { "cmd", "shell", "script", "terminal", "bash_command" },
        ["message"] = new[] { "commit_message", "msg", "title" },
        ["ref"] = new[] { "revision", "sha", "commit" },
        ["pathspec"] = new[] { "path", "files" },
        ["content"] = new[] { "text", "body", "new_content", "contents", "file_content" },
        ["max_results"] = new[] { "limit", "top_k", "topK", "maxResults" },
        ["start_line"] = new[] { "line_start", "from_line", "fromLine" },
        ["end_line"] = new[] { "line_end", "to_line", "toLine" },
        ["include_hidden"] = new[] { "hidden", "includeHidden" },
        ["create_if_missing"] = new[] { "createIfMissing" }
    };

    public static string? GetString(JsonElement args, string property)
    {
        if (TryGetValue(args, property, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object => value.GetRawText(),
                JsonValueKind.Array => value.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    public static int GetInt32(JsonElement args, string property, int fallback = 0)
    {
        if (!TryGetValue(args, property, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static bool GetBool(JsonElement args, string property, bool fallback = false)
    {
        if (!TryGetValue(args, property, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static bool HasValue(JsonElement args, string property)
    {
        return TryGetValue(args, property, out _);
    }

    private static bool TryGetValue(JsonElement args, string property, out JsonElement value)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        var queue = new Queue<JsonElement>();
        queue.Enqueue(args);

        var visited = 0;
        while (queue.Count > 0 && visited < 16)
        {
            visited++;
            var current = queue.Dequeue();
            if (current.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryFindProperty(current, property, out value))
            {
                return true;
            }

            if (TryGetAliasKeys(property, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    if (TryFindProperty(current, alias, out value))
                    {
                        return true;
                    }
                }
            }

            if (TryRecoverScalarFromInput(property, current, out value))
            {
                return true;
            }

            EnqueueNestedContainers(current, queue);
        }

        value = default;
        return false;
    }

    private static bool TryFindProperty(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.TryGetProperty(key, out var direct))
        {
            value = direct.Clone();
            return true;
        }

        var normalizedKey = NormalizeKey(key);
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                NormalizeKey(property.Name).Equals(normalizedKey, StringComparison.Ordinal))
            {
                value = property.Value.Clone();
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetAliasKeys(string property, out string[] aliases)
    {
        if (AliasMap.TryGetValue(property, out aliases!))
        {
            return true;
        }

        var normalized = NormalizeKey(property);
        foreach (var kv in AliasMap)
        {
            if (NormalizeKey(kv.Key).Equals(normalized, StringComparison.Ordinal))
            {
                aliases = kv.Value;
                return true;
            }
        }

        aliases = Array.Empty<string>();
        return false;
    }

    private static void EnqueueNestedContainers(JsonElement current, Queue<JsonElement> queue)
    {
        foreach (var key in NestedContainerKeys)
        {
            if (!TryFindProperty(current, key, out var nested))
            {
                continue;
            }

            if (nested.ValueKind == JsonValueKind.Object)
            {
                queue.Enqueue(nested);
                continue;
            }

            if (nested.ValueKind == JsonValueKind.String &&
                TryParseObjectFromString(nested.GetString(), out var parsed))
            {
                queue.Enqueue(parsed);
            }
        }
    }

    private static bool TryParseObjectFromString(string? raw, out JsonElement obj)
    {
        obj = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            obj = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRecoverScalarFromInput(string property, JsonElement current, out JsonElement value)
    {
        value = default;
        if (!TryFindProperty(current, "input", out var input) || input.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!TryExtractScalarFromText(input.GetString(), property, out var candidate))
        {
            return false;
        }

        using var wrapper = JsonDocument.Parse($"{{\"value\":{JsonSerializer.Serialize(candidate)}}}");
        value = wrapper.RootElement.GetProperty("value").Clone();
        return true;
    }

    private static bool TryExtractScalarFromText(string? raw, string property, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = NormalizeKey(property);
        if (normalized.Equals("path", StringComparison.Ordinal))
        {
            var candidate = ExtractPathFromText(raw);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate;
                return true;
            }
        }

        if (normalized.Equals("command", StringComparison.Ordinal) &&
            TryExtractCommandFromText(raw, out var command))
        {
            value = command;
            return true;
        }

        if (normalized.Equals("query", StringComparison.Ordinal) &&
            TryExtractQueryFromText(raw, out var query))
        {
            value = query;
            return true;
        }

        if (normalized.Equals("message", StringComparison.Ordinal) &&
            TryExtractCommitMessageFromText(raw, out var message))
        {
            value = message;
            return true;
        }

        if (normalized.Equals("ref", StringComparison.Ordinal) &&
            TryExtractRefFromText(raw, out var gitRef))
        {
            value = gitRef;
            return true;
        }

        if (normalized.Equals("content", StringComparison.Ordinal) &&
            TryExtractContentFromText(raw, out var content))
        {
            value = content;
            return true;
        }

        if (TryExtractByNamedKeys(raw, BuildKeyCandidates(property), out var fallback))
        {
            value = fallback;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> BuildKeyCandidates(string property)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            property
        };

        if (TryGetAliasKeys(property, out var aliases))
        {
            foreach (var alias in aliases)
            {
                set.Add(alias);
            }
        }

        return set.ToArray();
    }

    private static bool TryExtractByNamedKeys(string raw, IReadOnlyList<string> keys, out string value)
    {
        value = string.Empty;
        foreach (var key in keys)
        {
            var escaped = Regex.Escape(key);
            var quoted = Regex.Match(
                raw,
                $"(?is)[\"']{escaped}[\"']\\s*[:=]\\s*[\"'`](?<v>[^\"'`\\r\\n]{{1,6000}})[\"'`]");
            if (quoted.Success)
            {
                value = quoted.Groups["v"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            var line = Regex.Match(
                raw,
                $@"(?im)^\s*(?:[-*]\s*)?(?:[""'`])?{escaped}(?:[""'`])?\s*[:=]\s*(?<v>.+?)\s*$");
            if (line.Success)
            {
                value = line.Groups["v"].Value.Trim().Trim('"', '\'', '`');
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractCommandFromText(string raw, out string command)
    {
        command = string.Empty;
        if (TryExtractByNamedKeys(raw, BuildKeyCandidates("command"), out var byKey))
        {
            command = ClipScalar(byKey, 1000);
            return !string.IsNullOrWhiteSpace(command);
        }

        foreach (Match prompt in Regex.Matches(raw, @"(?im)^\s*(?:\$|PS>)\s*(?<cmd>.+)$"))
        {
            var candidate = prompt.Groups["cmd"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                command = ClipScalar(candidate, 1000);
                return true;
            }
        }

        foreach (Match match in Regex.Matches(raw, "```(?<lang>[^\\r\\n`]*)\\r?\\n(?<body>[\\s\\S]*?)```"))
        {
            var lang = (match.Groups["lang"].Value ?? string.Empty).Trim().ToLowerInvariant();
            var body = (match.Groups["body"].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (lang is not ("bash" or "sh" or "zsh" or "shell" or "powershell" or "pwsh" or "cmd"))
            {
                continue;
            }

            var lines = body
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart())
                .Select(line => line.StartsWith("$ ", StringComparison.Ordinal) ? line[2..] : line)
                .Select(line => line.StartsWith("PS> ", StringComparison.OrdinalIgnoreCase) ? line[4..] : line)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            if (lines.Length > 0)
            {
                command = ClipScalar(string.Join('\n', lines), 1000);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractQueryFromText(string raw, out string query)
    {
        query = string.Empty;
        if (TryExtractByNamedKeys(raw, BuildKeyCandidates("query"), out var byKey))
        {
            query = ClipScalar(byKey, 300);
            return !string.IsNullOrWhiteSpace(query);
        }

        var quoted = Regex.Match(raw, "(?im)\\bsearch(?:\\s+for)?\\s+[\"'`](?<q>[^\"'`\\r\\n]{2,300})[\"'`]");
        if (quoted.Success)
        {
            query = ClipScalar(quoted.Groups["q"].Value, 300);
            return true;
        }

        return false;
    }

    private static bool TryExtractCommitMessageFromText(string raw, out string message)
    {
        message = string.Empty;
        if (TryExtractByNamedKeys(raw, BuildKeyCandidates("message"), out var byKey))
        {
            message = ClipScalar(byKey, 180);
            return !string.IsNullOrWhiteSpace(message);
        }

        var quoted = Regex.Match(raw, "(?im)\\bcommit(?:\\s+message)?\\s*[:=]\\s*[\"'`](?<m>[^\"'`\\r\\n]{3,180})[\"'`]");
        if (quoted.Success)
        {
            message = quoted.Groups["m"].Value.Trim();
            return true;
        }

        return false;
    }

    private static bool TryExtractRefFromText(string raw, out string gitRef)
    {
        gitRef = string.Empty;
        if (TryExtractByNamedKeys(raw, BuildKeyCandidates("ref"), out var byKey))
        {
            gitRef = ClipScalar(byKey, 120);
            return !string.IsNullOrWhiteSpace(gitRef);
        }

        var head = Regex.Match(raw, @"\bHEAD(?:~\d+)?\b", RegexOptions.IgnoreCase);
        if (head.Success)
        {
            gitRef = head.Value.ToUpperInvariant();
            return true;
        }

        var hash = Regex.Match(raw, @"\b[0-9a-fA-F]{7,40}\b");
        if (hash.Success)
        {
            gitRef = hash.Value;
            return true;
        }

        return false;
    }

    private static bool TryExtractContentFromText(string raw, out string content)
    {
        content = string.Empty;
        if (TryExtractByNamedKeys(raw, BuildKeyCandidates("content"), out var byKey))
        {
            content = ClipScalar(byKey, 32000);
            return !string.IsNullOrWhiteSpace(content);
        }

        foreach (Match match in Regex.Matches(raw, "```(?<lang>[^\\r\\n`]*)\\r?\\n(?<body>[\\s\\S]*?)```"))
        {
            var lang = (match.Groups["lang"].Value ?? string.Empty).Trim().ToLowerInvariant();
            if (lang is "diff" or "patch" or "bash" or "sh" or "zsh" or "shell" or "powershell" or "pwsh" or "cmd")
            {
                continue;
            }

            var body = (match.Groups["body"].Value ?? string.Empty).Trim('\r', '\n');
            if (!string.IsNullOrWhiteSpace(body))
            {
                content = ClipScalar(body, 32000);
                return true;
            }
        }

        var block = Regex.Match(raw, @"(?ims)^\s*(?:content|body|text)\s*:\s*(?:\|\s*)?\r?\n(?<v>(?:[ \t].*(?:\r?\n|$))+)");
        if (block.Success)
        {
            var lines = block.Groups["v"].Value
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] : line.TrimStart('\t'))
                .ToArray();
            var candidate = string.Join('\n', lines).Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                content = ClipScalar(candidate, 32000);
                return true;
            }
        }

        return false;
    }

    private static string ClipScalar(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        if (maxChars <= 3)
        {
            return trimmed[..Math.Max(0, maxChars)];
        }

        return trimmed[..(maxChars - 3)] + "...";
    }

    private static string? ExtractPathFromText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim().Trim('"', '\'', '`');
        var prefixes = new[] { "path=", "path:", "file=", "file:", "filename=", "filename:" };
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim().Trim('"', '\'', '`');
                break;
            }
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains('\n') || text.Contains('\r'))
        {
            return null;
        }

        if (text.Contains(' ') && !text.Contains('/') && !text.Contains('\\'))
        {
            return null;
        }

        if (!text.Contains('/') &&
            !text.Contains('\\') &&
            !text.Contains('.') &&
            !text.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
            !text.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text;
    }

    private static string NormalizeKey(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }
}
