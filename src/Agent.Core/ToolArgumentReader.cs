using System.Text.Json;

namespace Agent.Core;

public static class ToolArgumentReader
{
    public static string? GetString(JsonElement args, string property)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    public static int GetInt32(JsonElement args, string property, int fallback = 0)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return fallback;
    }

    public static bool GetBool(JsonElement args, string property, bool fallback = false)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(property, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
        {
            return value.GetBoolean();
        }

        return fallback;
    }
}
