using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

public static class OpenAiCompatibleToolCallParser
{
    public static ModelAdapterTurnResult ParseNonStreaming(string raw, string fallbackModel, ToolCallingMode mode)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? fallbackModel : fallbackModel;
        var message = root.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? string.Empty
            : string.Empty;

        var blocks = new List<AssistantContentBlock>();
        if (!string.IsNullOrWhiteSpace(content))
        {
            blocks.Add(new TextBlock(content));
        }

        if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var callEl in toolCallsEl.EnumerateArray())
            {
                if (!TryParseNativeToolCall(callEl, out var block, out var error))
                {
                    return Invalid(raw, model, mode, error);
                }

                blocks.Add(block);
            }
        }

        var assistant = blocks.OfType<ToolCallBlock>().Any()
            ? new AssistantMessage(blocks, raw, mode, AssistantMessageKind.ToolCall)
            : new AssistantMessage(blocks, raw, mode, AssistantMessageKind.Final);

        return new ModelAdapterTurnResult(
            assistant,
            model,
            TryReadUsage(root, "prompt_tokens"),
            TryReadUsage(root, "completion_tokens"),
            TryReadUsage(root, "total_tokens"),
            raw,
            mode);
    }

    public static ModelAdapterTurnResult ParseStreaming(string raw, string fallbackModel)
    {
        var content = new StringBuilder();
        var calls = new Dictionary<int, StreamingToolCallAccumulator>();
        string model = fallbackModel;

        using var reader = new StringReader(raw);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
            {
                model = modelEl.GetString() ?? model;
            }

            if (!root.TryGetProperty("choices", out var choicesEl) ||
                choicesEl.ValueKind != JsonValueKind.Array ||
                choicesEl.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choicesEl[0];
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                content.Append(contentEl.GetString());
            }

            if (!delta.TryGetProperty("tool_calls", out var toolCallsEl) || toolCallsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var callEl in toolCallsEl.EnumerateArray())
            {
                var index = callEl.TryGetProperty("index", out var indexEl) && indexEl.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : calls.Count;
                if (!calls.TryGetValue(index, out var acc))
                {
                    acc = new StreamingToolCallAccumulator(index);
                    calls[index] = acc;
                }

                acc.ApplyDelta(callEl);
            }
        }

        var blocks = new List<AssistantContentBlock>();
        if (content.Length > 0)
        {
            blocks.Add(new TextBlock(content.ToString()));
        }

        foreach (var acc in calls.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            if (!acc.TryBuild(out var block, out var error))
            {
                return Invalid(raw, model, ToolCallingMode.NativeStreamingTools, error);
            }

            blocks.Add(block);
        }

        var assistant = blocks.OfType<ToolCallBlock>().Any()
            ? new AssistantMessage(blocks, raw, ToolCallingMode.NativeStreamingTools, AssistantMessageKind.ToolCall)
            : new AssistantMessage(blocks, raw, ToolCallingMode.NativeStreamingTools, AssistantMessageKind.Final);

        return new ModelAdapterTurnResult(assistant, model, Raw: raw, ToolCallingMode: ToolCallingMode.NativeStreamingTools);
    }

    private static bool TryParseNativeToolCall(JsonElement callEl, out ToolCallBlock block, out string error)
    {
        block = null!;
        error = string.Empty;
        var id = callEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Malformed native tool call: missing tool call id.";
            return false;
        }

        if (!callEl.TryGetProperty("function", out var functionEl) || functionEl.ValueKind != JsonValueKind.Object)
        {
            error = "Malformed native tool call: missing function object.";
            return false;
        }

        var name = functionEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Malformed native tool call: missing function name.";
            return false;
        }

        var rawArguments = functionEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String
            ? argsEl.GetString() ?? "{}"
            : "{}";

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Malformed native tool call: function arguments must be a JSON object.";
                return false;
            }

            block = new ToolCallBlock(new ToolCallId(id!), new ToolName(name!), doc.RootElement.Clone(), "native tool call");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Malformed native tool call: invalid JSON arguments. {ex.Message}";
            return false;
        }
    }

    private static ModelAdapterTurnResult Invalid(string raw, string model, ToolCallingMode mode, string error)
    {
        return new ModelAdapterTurnResult(
            new AssistantMessage(Array.Empty<AssistantContentBlock>(), raw, mode, AssistantMessageKind.Invalid, error),
            model,
            Raw: raw,
            ToolCallingMode: mode);
    }

    private static int? TryReadUsage(JsonElement root, string field)
    {
        if (!root.TryGetProperty("usage", out var usageEl) ||
            usageEl.ValueKind != JsonValueKind.Object ||
            !usageEl.TryGetProperty(field, out var tokenEl) ||
            tokenEl.ValueKind != JsonValueKind.Number ||
            !tokenEl.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }
}

