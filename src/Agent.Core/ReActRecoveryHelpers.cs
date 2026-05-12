using System.Text.Json;

namespace Agent.Core;

internal static class ReActRecoveryHelpers
{
    public static string BuildSeedSearchQuery(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "TODO";
        }

        var words = task
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= 4 && char.IsLetterOrDigit(w[0]))
            .Take(3)
            .ToArray();

        return words.Length == 0 ? "TODO" : string.Join(' ', words);
    }

    public static AgentDecision CreateToolDecision(string toolName, string reason, string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new AgentDecision(
            AgentDecisionType.Tool,
            toolName,
            doc.RootElement.Clone(),
            reason,
            string.Empty);
    }

    public static JsonElement MergeArguments(JsonElement source, IReadOnlyDictionary<string, object?> updates)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                map[property.Name] = property.Value.Clone();
            }
        }

        foreach (var update in updates)
        {
            map[update.Key] = JsonSerializer.SerializeToElement(update.Value);
        }

        var json = JsonSerializer.Serialize(map);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
