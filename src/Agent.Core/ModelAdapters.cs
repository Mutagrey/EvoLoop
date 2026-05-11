using System.Text.Json;

namespace Agent.Core;

public interface IModelAdapter
{
    ModelAdapterCapabilities AdapterCapabilities { get; }
    Task<ModelAdapterTurnResult> CompleteTurnAsync(ModelAdapterTurnRequest request, CancellationToken ct);
}

public interface IModelAdapterRouter
{
    IModelAdapter GetAdapter(string profileName, ToolCallingMode requestedMode);
}

public enum ToolCallingMode
{
    Auto,
    NativeNonStreamingTools,
    NativeStreamingTools,
    JsonReActFallback,
    PlainTextRecoveryFallback
}

public sealed record NativeToolSupport(bool NonStreaming, bool Streaming);

public sealed record JsonModeSupport(bool JsonResponseFormat);

public sealed record StreamingToolSupport(bool DeltaToolCalls);

public sealed record ModelAdapterCapabilities(
    NativeToolSupport NativeTools,
    JsonModeSupport JsonMode,
    StreamingToolSupport StreamingTools)
{
    public static ModelAdapterCapabilities JsonOnly { get; } = new(
        new NativeToolSupport(false, false),
        new JsonModeSupport(true),
        new StreamingToolSupport(false));

    public bool Supports(ToolCallingMode mode) => mode switch
    {
        ToolCallingMode.Auto => true,
        ToolCallingMode.NativeNonStreamingTools => NativeTools.NonStreaming,
        ToolCallingMode.NativeStreamingTools => NativeTools.Streaming,
        ToolCallingMode.JsonReActFallback => true,
        ToolCallingMode.PlainTextRecoveryFallback => true,
        _ => false
    };
}

public sealed record ModelToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    bool IsFallbackOnly);

public sealed record ModelAdapterTurnRequest(
    string ProfileName,
    string Model,
    string SystemPrompt,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<InternalMessage> InternalMessages,
    IReadOnlyCollection<ITool> Tools,
    ToolCallingMode ToolCallingMode,
    double Temperature,
    int MaxTokens,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ModelAdapterTurnResult(
    AssistantMessage AssistantMessage,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    string? Raw = null,
    ToolCallingMode ToolCallingMode = ToolCallingMode.JsonReActFallback);

public sealed class ModelClientAdapterRouter : IModelAdapterRouter
{
    private readonly IModelClientRouter _router;
    private readonly Dictionary<string, IModelAdapter> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModelClientAdapterRouter(IModelClientRouter router)
    {
        _router = router;
    }

    public IModelAdapter GetAdapter(string profileName, ToolCallingMode requestedMode)
    {
        var client = _router.GetClient(profileName);
        if (client is IModelAdapter adapter)
        {
            return adapter;
        }

        if (_cache.TryGetValue(profileName, out var cached))
        {
            return cached;
        }

        var wrapped = new ModelClientBackedAdapter(client);
        _cache[profileName] = wrapped;
        return wrapped;
    }
}

public sealed class ModelClientBackedAdapter : IModelAdapter
{
    private readonly IModelClient _client;

    public ModelClientBackedAdapter(IModelClient client)
    {
        _client = client;
    }

    public ModelAdapterCapabilities AdapterCapabilities => ModelAdapterCapabilities.JsonOnly;

    public async Task<ModelAdapterTurnResult> CompleteTurnAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        var result = await _client.CompleteAsync(new ModelTurnRequest(
            request.ProfileName,
            request.Model,
            request.SystemPrompt,
            request.Messages,
            request.Temperature,
            request.MaxTokens,
            request.Metadata), ct);

        var assistant = JsonReActResponseParser.Parse(
            result.Content,
            request.Tools.Select(tool => tool.Name),
            allowPlainTextRecovery: true,
            mode: ToolCallingMode.JsonReActFallback);

        return new ModelAdapterTurnResult(
            assistant,
            result.Model,
            result.PromptTokens,
            result.CompletionTokens,
            result.TotalTokens,
            result.Raw,
            ToolCallingMode.JsonReActFallback);
    }
}
