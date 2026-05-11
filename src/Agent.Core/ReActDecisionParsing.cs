using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core;

internal sealed record PendingToolTurn(
    AgentDecision Decision,
    AssistantMessage AssistantMessage,
    string ModelContent,
    ToolCallingMode ToolCallingMode);

internal enum AgentDecisionType
{
    Tool,
    Final,
    Clarify,
    Invalid
}

internal sealed record AgentDecision(AgentDecisionType Type, string ToolName, JsonElement Arguments, string Reason, string Message, string? ToolCallId = null)
{
    public static AgentDecision Final(string message)
    {
        return new AgentDecision(AgentDecisionType.Final, string.Empty, default, string.Empty, message);
    }

    public static AgentDecision Invalid(string message)
    {
        return new AgentDecision(AgentDecisionType.Invalid, string.Empty, default, string.Empty, message);
    }
}

internal static class AgentDecisionParser
{
    private static readonly JsonDocument EmptyObject = JsonDocument.Parse("{}");

    public static AgentDecision Parse(string content, IEnumerable<string>? toolNames = null, bool allowPlainTextRecovery = true)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AgentDecision.Invalid("Empty model response.");
        }

        if (!TryParseJson(content, out var document))
        {
            var recovered = allowPlainTextRecovery ? TryParseFromText(content, toolNames) : null;
            return recovered ?? AgentDecision.Invalid("Response is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            var allowedTools = toolNames is null
                ? null
                : new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
            if (TryParseToolCallVariants(root, allowedTools, out var variantDecision))
            {
                return variantDecision;
            }

            if (!root.TryGetProperty("type", out var typeEl))
            {
                return AgentDecision.Invalid("JSON missing required 'type' property.");
            }

            var type = typeEl.GetString()?.Trim().ToLowerInvariant();
            return type switch
            {
                "final" => AgentDecision.Final(root.TryGetProperty("message", out var finalMsg) ? finalMsg.GetString() ?? string.Empty : string.Empty),
                "clarify" => new AgentDecision(
                    AgentDecisionType.Clarify,
                    string.Empty,
                    EmptyObject.RootElement.Clone(),
                    string.Empty,
                    root.TryGetProperty("message", out var clarifyMsg) ? clarifyMsg.GetString() ?? string.Empty : string.Empty),
                "tool" => new AgentDecision(
                    AgentDecisionType.Tool,
                    root.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("arguments", out var argsEl) ? NormalizeArguments(argsEl) : EmptyObject.RootElement.Clone(),
                    root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? string.Empty : string.Empty,
                    string.Empty),
                _ => AgentDecision.Invalid("Unknown decision type.")
            };
        }
    }

    public static AgentDecision ParsePlainTextRecovery(string content, IEnumerable<string>? toolNames = null)
    {
        return TryParseFromText(content, toolNames) ?? AgentDecision.Invalid("Response is not recoverable plain-text tool output.");
    }

    private static bool TryParseJson(string content, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(content);
            return true;
        }
        catch
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var candidate = content[start..(end + 1)];
                try
                {
                    document = JsonDocument.Parse(candidate);
                    return true;
                }
                catch
                {
                    // ignored
                }
            }
        }

        document = null!;
        return false;
    }

    private static bool TryParseToolCallVariants(
        JsonElement root,
        HashSet<string>? allowedTools,
        out AgentDecision decision)
    {
        return TryParseToolCallVariants(root, allowedTools, 0, out decision);
    }

    private static bool TryParseToolCallVariants(
        JsonElement root,
        HashSet<string>? allowedTools,
        int depth,
        out AgentDecision decision)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            decision = AgentDecision.Invalid("Root JSON is not an object.");
            return false;
        }

        if (depth > 2)
        {
            decision = AgentDecision.Invalid("Tool-call variant nesting too deep.");
            return false;
        }

        if (TryBuildToolDecision(root, "tool", allowedTools, out decision))
        {
            return true;
        }

        if (TryBuildToolDecision(root, "action", allowedTools, out decision))
        {
            return true;
        }

        if (TryBuildToolDecision(root, "name", allowedTools, out decision))
        {
            return true;
        }

        if (root.TryGetProperty("function_call", out var functionCall) &&
            functionCall.ValueKind == JsonValueKind.Object &&
            TryBuildToolDecision(functionCall, "name", allowedTools, out decision))
        {
            return true;
        }

        if (root.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (call.TryGetProperty("function", out var functionObj) &&
                    functionObj.ValueKind == JsonValueKind.Object &&
                    TryBuildToolDecision(functionObj, "name", allowedTools, out decision))
                {
                    return true;
                }

                if (TryBuildToolDecision(call, "name", allowedTools, out decision))
                {
                    return true;
                }
            }
        }

        if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString() ?? string.Empty;
            if ((type.Equals("tool_call", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("function_call", StringComparison.OrdinalIgnoreCase)) &&
                TryBuildToolDecision(root, "name", allowedTools, out decision))
            {
                return true;
            }
        }

        var nestedKeys = new[] { "decision", "response", "output", "result" };
        foreach (var key in nestedKeys)
        {
            if (!root.TryGetProperty(key, out var nested))
            {
                continue;
            }

            if (nested.ValueKind == JsonValueKind.Object &&
                TryParseToolCallVariants(nested, allowedTools, depth + 1, out decision))
            {
                return true;
            }

            if (nested.ValueKind == JsonValueKind.String &&
                TryParseJson(nested.GetString() ?? string.Empty, out var nestedDoc))
            {
                using (nestedDoc)
                {
                    if (TryParseToolCallVariants(nestedDoc.RootElement, allowedTools, depth + 1, out decision))
                    {
                        return true;
                    }
                }
            }
        }

        if (root.TryGetProperty("content", out var contentEl) &&
            contentEl.ValueKind == JsonValueKind.String &&
            TryParseJson(contentEl.GetString() ?? string.Empty, out var contentDoc))
        {
            using (contentDoc)
            {
                if (TryParseToolCallVariants(contentDoc.RootElement, allowedTools, depth + 1, out decision))
                {
                    return true;
                }
            }
        }

        if (!root.TryGetProperty("type", out _) &&
            root.TryGetProperty("message", out var messageEl) &&
            messageEl.ValueKind == JsonValueKind.String)
        {
            decision = AgentDecision.Final(messageEl.GetString() ?? string.Empty);
            return true;
        }

        decision = AgentDecision.Invalid("No known tool-call variant.");
        return false;
    }

    private static bool TryBuildToolDecision(
        JsonElement root,
        string key,
        HashSet<string>? allowedTools,
        out AgentDecision decision)
    {
        if (!root.TryGetProperty(key, out var toolEl) || toolEl.ValueKind != JsonValueKind.String)
        {
            decision = AgentDecision.Invalid("Tool key not found.");
            return false;
        }

        var toolName = toolEl.GetString() ?? string.Empty;
        if (allowedTools is not null && allowedTools.Count > 0 && !allowedTools.Contains(toolName))
        {
            decision = AgentDecision.Invalid("Tool name is not allowed.");
            return false;
        }

        if (key.Equals("name", StringComparison.OrdinalIgnoreCase) &&
            !root.TryGetProperty("arguments", out _) &&
            !root.TryGetProperty("args", out _) &&
            !root.TryGetProperty("action_input", out _) &&
            !root.TryGetProperty("input", out _))
        {
            decision = AgentDecision.Invalid("Name key is not a tool call.");
            return false;
        }

        var args = root.TryGetProperty("arguments", out var argsEl) ? NormalizeArguments(argsEl) :
            root.TryGetProperty("args", out var argsAltEl) ? NormalizeArguments(argsAltEl) :
            root.TryGetProperty("action_input", out var actionInputEl) ? NormalizeArguments(actionInputEl) :
            root.TryGetProperty("input", out var inputEl) ? NormalizeArguments(inputEl) :
            EmptyObject.RootElement.Clone();

        var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString() ?? "recovered tool decision"
            : "recovered tool decision";

        decision = new AgentDecision(
            AgentDecisionType.Tool,
            toolName,
            args,
            reason,
            string.Empty);
        return true;
    }

    private static AgentDecision? TryParseFromText(string content, IEnumerable<string>? toolNames)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var finalMatch = Regex.Match(trimmed, @"^\s*(final|done|completed)\s*[:\-]\s*(.+)$", RegexOptions.IgnoreCase);
        if (finalMatch.Success)
        {
            return AgentDecision.Final(finalMatch.Groups[2].Value.Trim());
        }

        var toolName = TryFindToolName(trimmed, toolNames);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        var arguments = TryExtractArgumentsObject(trimmed) ?? EmptyObject.RootElement.Clone();
        var reason = "recovered from non-JSON model output";
        return new AgentDecision(AgentDecisionType.Tool, toolName, arguments, reason, string.Empty);
    }

    private static string? TryFindToolName(string text, IEnumerable<string>? toolNames)
    {
        var toolList = toolNames?.ToList() ?? new List<string>();
        if (toolList.Count == 0)
        {
            return null;
        }

        var actionTag = Regex.Match(text, @"(?im)^\s*(tool|action)\s*[:=]\s*([a-zA-Z0-9_\-]+)\s*$");
        if (actionTag.Success)
        {
            var candidate = actionTag.Groups[2].Value.Trim();
            if (toolList.Any(t => t.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return toolList.First(t => t.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            }
        }

        foreach (var tool in toolList.OrderByDescending(t => t.Length))
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(tool)}\b", RegexOptions.IgnoreCase))
            {
                return tool;
            }
        }

        return null;
    }

    private static JsonElement? TryExtractArgumentsObject(string text)
    {
        var argsTag = Regex.Match(text, @"(?is)(arguments|args)\s*[:=]\s*(\{.*\})");
        if (argsTag.Success)
        {
            var fromTag = argsTag.Groups[2].Value;
            if (TryParseJson(fromTag, out var docFromTag))
            {
                using (docFromTag)
                {
                    return docFromTag.RootElement.Clone();
                }
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var candidate = text[start..(end + 1)];
            if (TryParseJson(candidate, out var doc))
            {
                using (doc)
                {
                    return doc.RootElement.Clone();
                }
            }
        }

        return null;
    }

    private static JsonElement NormalizeArguments(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            return source.Clone();
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            var str = source.GetString() ?? string.Empty;
            if (TryParseJson(str, out var doc))
            {
                using (doc)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        return doc.RootElement.Clone();
                    }
                }
            }

            using var wrapped = JsonDocument.Parse($"{{\"input\":{JsonSerializer.Serialize(str)}}}");
            return wrapped.RootElement.Clone();
        }

        var raw = source.GetRawText();
        using var wrappedFallback = JsonDocument.Parse($"{{\"input\":{raw}}}");
        return wrappedFallback.RootElement.Clone();
    }
}

public static class JsonReActResponseParser
{
    public static AssistantMessage Parse(
        string content,
        IEnumerable<string>? toolNames = null,
        bool allowPlainTextRecovery = false,
        ToolCallingMode mode = ToolCallingMode.JsonReActFallback)
    {
        var decision = AgentDecisionParser.Parse(content, toolNames, allowPlainTextRecovery);
        return ToAssistantMessage(decision, content, mode);
    }

    internal static AssistantMessage ToAssistantMessage(AgentDecision decision, string rawContent, ToolCallingMode mode)
    {
        return decision.Type switch
        {
            AgentDecisionType.Tool => new AssistantMessage(
                new AssistantContentBlock[]
                {
                    new ToolCallBlock(
                        string.IsNullOrWhiteSpace(decision.ToolCallId)
                            ? ToolCallId.CreateFallback()
                            : new ToolCallId(decision.ToolCallId!),
                        new ToolName(decision.ToolName),
                        decision.Arguments,
                        decision.Reason)
                },
                rawContent,
                mode,
                AssistantMessageKind.ToolCall),
            AgentDecisionType.Final => new AssistantMessage(
                new AssistantContentBlock[] { new TextBlock(decision.Message) },
                rawContent,
                mode,
                AssistantMessageKind.Final),
            AgentDecisionType.Clarify => new AssistantMessage(
                new AssistantContentBlock[] { new TextBlock(decision.Message) },
                rawContent,
                mode,
                AssistantMessageKind.Clarify),
            _ => new AssistantMessage(
                Array.Empty<AssistantContentBlock>(),
                rawContent,
                mode,
                AssistantMessageKind.Invalid,
                decision.Message)
        };
    }
}

public static class PlainTextRecoveryParser
{
    public static AssistantMessage Parse(string content, IEnumerable<string>? toolNames = null)
    {
        var decision = AgentDecisionParser.ParsePlainTextRecovery(content, toolNames);
        return JsonReActResponseParser.ToAssistantMessage(
            decision,
            content,
            ToolCallingMode.PlainTextRecoveryFallback);
    }
}
