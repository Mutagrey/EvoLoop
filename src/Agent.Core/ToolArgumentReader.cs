using System.Text.Json;

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
        ["path"] = new[] { "file", "file_path", "filepath", "filename", "target", "target_path", "relative_path", "name" },
        ["query"] = new[] { "text", "pattern", "keyword", "search", "term" },
        ["command"] = new[] { "cmd", "shell", "script" },
        ["message"] = new[] { "commit_message", "msg" },
        ["ref"] = new[] { "revision", "sha", "commit" },
        ["pathspec"] = new[] { "path", "files" },
        ["content"] = new[] { "text", "body", "new_content" },
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

            if (TryRecoverPathFromInput(property, current, out value))
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

    private static bool TryRecoverPathFromInput(string property, JsonElement current, out JsonElement value)
    {
        value = default;
        if (!NormalizeKey(property).Equals("path", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryFindProperty(current, "input", out var input) || input.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = ExtractPathFromText(input.GetString());
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        using var wrapper = JsonDocument.Parse($"{{\"value\":{JsonSerializer.Serialize(candidate)}}}");
        value = wrapper.RootElement.GetProperty("value").Clone();
        return true;
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
