using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

internal sealed class StreamingToolCallAccumulator
{
    private readonly StringBuilder _arguments = new();

    public StreamingToolCallAccumulator(int index)
    {
        Index = index;
    }

    public int Index { get; }
    public string? Id { get; private set; }
    public string? Name { get; private set; }

    public void ApplyDelta(JsonElement delta)
    {
        if (delta.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            Id = idEl.GetString() ?? Id;
        }

        if (!delta.TryGetProperty("function", out var functionEl) || functionEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (functionEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            Name = nameEl.GetString() ?? Name;
        }

        if (functionEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
        {
            _arguments.Append(argsEl.GetString());
        }
    }

    public bool TryBuild(out ToolCallBlock block, out string error)
    {
        block = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = $"Malformed streaming native tool call at index {Index}: missing tool call id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = $"Malformed streaming native tool call at index {Index}: missing function name.";
            return false;
        }

        var rawArgs = _arguments.Length == 0 ? "{}" : _arguments.ToString();
        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Malformed streaming native tool call at index {Index}: arguments must be a JSON object.";
                return false;
            }

            block = new ToolCallBlock(
                new ToolCallId(Id!),
                new ToolName(Name!),
                doc.RootElement.Clone(),
                "native streaming tool call",
                new Dictionary<string, string> { ["index"] = Index.ToString() });
            return true;
        }
        catch (Exception ex)
        {
            error = $"Malformed streaming native tool call at index {Index}: invalid JSON arguments. {ex.Message}";
            return false;
        }
    }
}

internal sealed class ProbeTool : ITool
{
    public string Name => "evoloop_probe_noop";
    public ToolSchema Schema => new(
        "Safe no-op probe used by EvoLoop to detect native tool-call support.",
        Array.Empty<string>(),
        new Dictionary<string, string> { ["ok"] = "Any boolean marker." });
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Status, false, Array.Empty<string>());

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(true, "probe noop"));
}

