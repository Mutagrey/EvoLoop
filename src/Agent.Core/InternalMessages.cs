using System.Text.Json;

namespace Agent.Core;

public abstract record InternalMessage(string Role);

public sealed record UserMessage(string Content) : InternalMessage("user");

public sealed record AssistantMessage(
    IReadOnlyList<AssistantContentBlock> ContentBlocks,
    string? RawContent = null,
    ToolCallingMode ToolCallingMode = ToolCallingMode.JsonReActFallback,
    AssistantMessageKind Kind = AssistantMessageKind.Content,
    string? Error = null) : InternalMessage("assistant")
{
    public IReadOnlyList<ToolCallBlock> ToolCalls =>
        ContentBlocks.OfType<ToolCallBlock>().ToArray();

    public string Text => string.Join(
        Environment.NewLine,
        ContentBlocks.OfType<TextBlock>().Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
}

public sealed record ToolResultMessage(
    ToolCallId ToolCallId,
    ToolName ToolName,
    ToolResultContent Content,
    bool IsError,
    IReadOnlyDictionary<string, string>? Metadata = null) : InternalMessage("tool")
{
    public static ToolResultMessage FromToolResult(ToolCallBlock call, ToolResult result)
    {
        return new ToolResultMessage(
            call.Id,
            call.Name,
            ToolResultContent.FromToolResult(result),
            !result.Success,
            result.Metadata);
    }

    public string ToObservationText(int maxChars)
    {
        var parts = new List<string>
        {
            $"success: {!IsError}",
            $"message: {Content.Message}"
        };

        if (!string.IsNullOrWhiteSpace(Content.StdOut))
        {
            parts.Add("stdout:");
            parts.Add(Content.StdOut!);
        }

        if (!string.IsNullOrWhiteSpace(Content.StdErr))
        {
            parts.Add("stderr:");
            parts.Add(Content.StdErr!);
        }

        var text = string.Join(Environment.NewLine, parts);
        return text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 14)] + Environment.NewLine + "[truncated]";
    }
}

public abstract record AssistantContentBlock(string Type);

public sealed record TextBlock(string Text) : AssistantContentBlock("text");

public sealed record ThinkingBlock(string Text) : AssistantContentBlock("thinking");

public sealed record ToolCallBlock(
    ToolCallId Id,
    ToolName Name,
    JsonElement Arguments,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Metadata = null) : AssistantContentBlock("tool_call")
{
    public ToolCall ToToolCall() => new(Name.Value, Arguments, Reason ?? "model tool call");
}

public readonly record struct ToolCallId(string Value)
{
    public override string ToString() => Value;

    public static ToolCallId CreateFallback() => new("call_" + Guid.NewGuid().ToString("n"));
}

public readonly record struct ToolName(string Value)
{
    public override string ToString() => Value;
}

public sealed record ToolResultContent(
    string Message,
    string? StdOut = null,
    string? StdErr = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static ToolResultContent FromToolResult(ToolResult result)
        => new(result.Message, result.StdOut, result.StdErr, result.Metadata);
}

public enum AssistantMessageKind
{
    Content,
    ToolCall,
    Final,
    Clarify,
    Invalid
}

