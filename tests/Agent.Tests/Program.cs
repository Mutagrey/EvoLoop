using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using Agent.Core;
using Agent.Tools;

AssemblyLoadContext.Default.Resolving += ResolveFromOutput;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Policy denies outside workspace", TestPolicyDeniesOutsideWorkspace),
    ("Policy requires approval for writes", TestPolicyRequiresApprovalForWrite),
    ("ReAct loop handles final response", TestLoopFinalResponse),
    ("ReAct loop executes tool then final", TestLoopToolThenFinal),
    ("Fallback lexical search returns results", TestFallbackLexicalSearch)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"Tests failed: {failed}");
    return 1;
}

Console.WriteLine("All tests passed.");
return 0;

static Assembly? ResolveFromOutput(AssemblyLoadContext context, AssemblyName name)
{
    if (string.IsNullOrWhiteSpace(name.Name))
    {
        return null;
    }

    var candidate = Path.Combine(AppContext.BaseDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
}

static Task TestPolicyDeniesOutsideWorkspace()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);

    using var doc = JsonDocument.Parse("{\"path\":\"../secret.txt\"}");
    var call = new ToolCall("fs_read", doc.RootElement.Clone(), "test");
    var context = new ToolContext(
        WorkspaceRoot: "/tmp/workspace",
        SessionId: "s1",
        ProfileName: "reasoning",
        Config: config,
        SearchService: new NullSearchService());

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny decision for outside workspace path.");
    return Task.CompletedTask;
}

static Task TestPolicyRequiresApprovalForWrite()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);

    using var doc = JsonDocument.Parse("{\"path\":\"file.txt\",\"content\":\"x\"}");
    var call = new ToolCall("fs_write", doc.RootElement.Clone(), "write");
    var context = new ToolContext(
        WorkspaceRoot: "/tmp/workspace",
        SessionId: "s1",
        ProfileName: "reasoning",
        Config: config,
        SearchService: new NullSearchService());

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.RequireApproval, "Expected approval requirement for fs_write.");
    return Task.CompletedTask;
}

static async Task TestLoopFinalResponse()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        Array.Empty<ITool>(),
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("say hi", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(result.Success, "Expected final result success.");
    Assert(result.FinalMessage.Contains("done", StringComparison.OrdinalIgnoreCase), "Expected final message to contain done.");
}

static async Task TestLoopToolThenFinal()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"test\",\"arguments\":{\"value\":\"ok\"}}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"complete\"}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var tool = new EchoTool();
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { tool },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("run", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(result.Success, "Expected success after tool and final.");
    Assert(result.StepTrace.Count == 1, "Expected one tool step in trace.");
}

static async Task TestFallbackLexicalSearch()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        var file = Path.Combine(temp, "sample.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta query\ngamma\nquery again");

        var config = new AgentConfig();
        var service = new HybridSearchService(new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake"), config, temp);
        var hits = await service.LexicalAsync(new SearchQuery(temp, "query", 5), CancellationToken.None);

        Assert(hits.Count > 0, "Expected lexical hits.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeModelClient : IModelClient
{
    private readonly Queue<ModelTurnResult> _responses;

    public FakeModelClient(Queue<ModelTurnResult> responses)
    {
        _responses = responses;
    }

    public ModelCapabilities Capabilities => new(false, false);

    public Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct)
    {
        if (_responses.Count == 0)
        {
            return Task.FromResult(new ModelTurnResult("{\"type\":\"final\",\"message\":\"empty\"}", "fake"));
        }

        return Task.FromResult(_responses.Dequeue());
    }
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

internal sealed class EchoTool : ITool
{
    public string Name => "echo";

    public ToolSchema Schema => new("Echo value", new[] { "value" }, new Dictionary<string, string>());

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var value = ToolArgumentReader.GetString(call.Arguments, "value") ?? string.Empty;
        return Task.FromResult(new ToolResult(true, "ok", value));
    }
}
