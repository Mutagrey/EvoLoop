using System.Text.Json;

namespace Agent.Core;

public static class ToolSchemaJsonSchemaConverter
{
    public static JsonElement ToJsonSchema(ITool tool)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in tool.Schema.FieldDescriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            properties[field.Key] = new Dictionary<string, object?>
            {
                ["type"] = InferJsonType(field.Key),
                ["description"] = field.Value
            };
        }

        foreach (var required in tool.Schema.RequiredFields)
        {
            if (!properties.ContainsKey(required))
            {
                properties[required] = new Dictionary<string, object?>
                {
                    ["type"] = InferJsonType(required)
                };
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = tool.Schema.RequiredFields.ToArray(),
            ["additionalProperties"] = false
        };

        if (tool.Name.Equals("fs_patch", StringComparison.OrdinalIgnoreCase))
        {
            schema["anyOf"] = new object[]
            {
                new Dictionary<string, object?> { ["required"] = new[] { "unified_diff" } },
                new Dictionary<string, object?> { ["required"] = new[] { "content" } }
            };
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schema));
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<ModelToolDefinition> ToToolDefinitions(IEnumerable<ITool> tools)
    {
        return tools
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tool => new ModelToolDefinition(
                tool.Name,
                tool.Schema.Description,
                ToJsonSchema(tool),
                tool.Metadata.IsFallbackOnly))
            .ToArray();
    }

    private static string InferJsonType(string fieldName)
    {
        var normalized = fieldName.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "recurse" or "recursive" or "include_hidden" or "staged" or "create_if_missing" => "boolean",
            "timeout_sec" or "max_results" or "start_line" or "end_line" or "max_count" or "max_bytes" => "integer",
            _ => "string"
        };
    }
}

public static class ToolArgumentValidator
{
    public static IReadOnlyList<string> Validate(ITool tool, JsonElement arguments)
    {
        var errors = new List<string>();
        foreach (var required in tool.Schema.RequiredFields)
        {
            if (!ToolArgumentReader.HasValue(arguments, required))
            {
                errors.Add($"missing_required:{required}");
            }
        }

        if (tool.Name.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(arguments, "unified_diff") &&
            !ToolArgumentReader.HasValue(arguments, "content"))
        {
            errors.Add("missing_either:unified_diff_or_content");
        }

        if (tool.Name.Equals("exec_shell", StringComparison.OrdinalIgnoreCase) &&
            ToolArgumentReader.GetInt32(arguments, "timeout_sec", 1) < 1)
        {
            errors.Add("invalid_type_or_range:timeout_sec");
        }

        return errors;
    }
}

