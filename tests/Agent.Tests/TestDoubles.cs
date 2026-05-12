using Agent.Core;
using System.Net;

internal sealed class FakeModelClient : IModelClient
{
    private readonly Queue<ModelTurnResult> _responses;
    public List<ModelTurnRequest> SeenRequests { get; } = new();

    public FakeModelClient(Queue<ModelTurnResult> responses)
    {
        _responses = responses;
    }

    public ModelCapabilities Capabilities => new(false, false);

    public Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct)
    {
        SeenRequests.Add(request);
        if (_responses.Count == 0)
        {
            return Task.FromResult(new ModelTurnResult("{\"type\":\"final\",\"message\":\"empty\"}", "fake"));
        }

        return Task.FromResult(_responses.Dequeue());
    }
}

internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _respond;

    public RecordingHttpHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<string> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return _respond(RequestBodies.Count, request);
    }
}

internal sealed class FakeModelAdapter : IModelAdapter
{
    private readonly Queue<ModelAdapterTurnResult> _responses;
    public List<ModelAdapterTurnRequest> SeenRequests { get; } = new();

    public FakeModelAdapter(Queue<ModelAdapterTurnResult> responses)
    {
        _responses = responses;
    }

    public ModelAdapterCapabilities AdapterCapabilities => new(
        new NativeToolSupport(true, true),
        new JsonModeSupport(true),
        new StreamingToolSupport(true));

    public Task<ModelAdapterTurnResult> CompleteTurnAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        SeenRequests.Add(request);
        if (_responses.Count == 0)
        {
            return Task.FromResult(new ModelAdapterTurnResult(
                new AssistantMessage(new AssistantContentBlock[] { new TextBlock("empty") }, "empty", request.ToolCallingMode, AssistantMessageKind.Final),
                request.Model,
                ToolCallingMode: request.ToolCallingMode));
        }

        return Task.FromResult(_responses.Dequeue());
    }
}

internal sealed class FakeModelAdapterRouter : IModelAdapterRouter
{
    private readonly IModelAdapter _adapter;

    public FakeModelAdapterRouter(IModelAdapter adapter)
    {
        _adapter = adapter;
    }

    public IModelAdapter GetAdapter(string profileName, ToolCallingMode requestedMode) => _adapter;
}

internal sealed class FakeModelRouter : IModelClientRouter
{
    private readonly IModelClient _client;
    private readonly string _model;

    public FakeModelRouter(IModelClient client, string model)
    {
        _client = client;
        _model = model;
    }

    public IModelClient GetClient(string profileName) => _client;

    public string ResolveModelName(string profileName) => _model;
}

internal sealed class MultiProfileModelRouter : IModelClientRouter
{
    private readonly IReadOnlyDictionary<string, IModelClient> _clients;

    public MultiProfileModelRouter(IReadOnlyDictionary<string, IModelClient> clients)
    {
        _clients = clients;
    }

    public IModelClient GetClient(string profileName)
    {
        if (_clients.TryGetValue(profileName, out var client))
        {
            return client;
        }

        throw new InvalidOperationException($"Missing test model client for profile '{profileName}'.");
    }

    public string ResolveModelName(string profileName) => profileName;
}

internal sealed class AutoApproveService : IApprovalService
{
    private readonly bool _approve;

    public AutoApproveService(bool approve)
    {
        _approve = approve;
    }

    public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(_approve);
}

internal sealed class InMemoryEventStore : IEventStore
{
    public List<SessionStep> Steps { get; } = new();

    public Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
        => Task.FromResult(new SessionInfo(Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow));

    public Task AppendStepAsync(SessionStep step, CancellationToken ct)
    {
        Steps.Add(step);
        return Task.CompletedTask;
    }

    public Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct)
        => Task.CompletedTask;
}

internal sealed class NullSearchService : ISearchService
{
    public Task<IReadOnlyList<SearchHit>> LexicalAsync(SearchQuery query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>());

    public Task<IReadOnlyList<SearchHit>> RerankAsync(string task, IReadOnlyList<SearchHit> candidates, CancellationToken ct)
        => Task.FromResult(candidates);
}

internal sealed class FakeMemoryStore : IWorkspaceMemoryStore
{
    private readonly WorkspaceMemoryContext _context;
    public List<WorkspaceMemoryRecord> Saved { get; } = new();

    public FakeMemoryStore(WorkspaceMemoryContext context)
    {
        _context = context;
    }

    public Task<WorkspaceMemoryContext> LoadContextAsync(string workspaceRoot, string task, CancellationToken ct)
        => Task.FromResult(_context);

    public Task SaveRunAsync(WorkspaceMemoryRecord record, CancellationToken ct)
    {
        Saved.Add(record);
        return Task.CompletedTask;
    }
}

internal sealed class EchoTool : ITool
{
    public string Name => "echo";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Status, false, Array.Empty<string>());

    public ToolSchema Schema => new("Echo value", new[] { "value" }, new Dictionary<string, string>());

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var value = ToolArgumentReader.GetString(call.Arguments, "value") ?? string.Empty;
        return Task.FromResult(new ToolResult(true, "ok", value));
    }
}
