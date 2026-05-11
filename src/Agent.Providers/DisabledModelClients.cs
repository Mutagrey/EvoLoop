using Agent.Core;

namespace Agent.Providers;

public sealed class DisabledModelClientRouter : IModelClientRouter, IModelAdapterRouter
{
    private readonly IModelClient _client;
    private readonly string _message;

    public DisabledModelClientRouter(string message)
    {
        _message = message;
        _client = new DisabledModelClient(message);
    }

    public IModelClient GetClient(string profileName) => _client;

    public string ResolveModelName(string profileName) => $"disabled:{profileName}";

    public IModelAdapter GetAdapter(string profileName, ToolCallingMode requestedMode)
        => new ModelClientBackedAdapter(_client);
}

internal sealed class DisabledModelClient : IModelClient
{
    private readonly string _message;

    public DisabledModelClient(string message)
    {
        _message = message;
    }

    public ModelCapabilities Capabilities => new(false, false);

    public Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct)
    {
        throw new InvalidOperationException(_message);
    }
}

