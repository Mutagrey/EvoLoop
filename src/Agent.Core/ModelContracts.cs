namespace Agent.Core;

public interface IModelClient
{
    Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct);
    ModelCapabilities Capabilities { get; }
}

public interface IModelClientRouter
{
    IModelClient GetClient(string profileName);
    string ResolveModelName(string profileName);
}

public sealed record ModelCapabilities(
    bool SupportsStreaming,
    bool SupportsEmbeddings,
    NativeToolSupport? NativeTools = null,
    JsonModeSupport? JsonMode = null);

public sealed record ModelMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    IReadOnlyList<ToolCallBlock>? ToolCalls = null);

public sealed record ModelTurnRequest(
    string ProfileName,
    string Model,
    string SystemPrompt,
    IReadOnlyList<ModelMessage> Messages,
    double Temperature,
    int MaxTokens,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ModelTurnResult(
    string Content,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    string? Raw = null);
