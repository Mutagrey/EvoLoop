using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using Agent.Core;
using Agent.Tools;

AssemblyLoadContext.Default.Resolving += ResolveFromOutput;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Policy denies outside workspace", TestPolicyDeniesOutsideWorkspace),
    ("Policy denies sibling path prefix bypass", TestPolicyDeniesSiblingPrefixBypass),
    ("Policy denies exec_shell cwd outside workspace", TestPolicyDeniesExecShellCwdOutsideWorkspace),
    ("Policy denies destructive shell patterns", TestPolicyDeniesDestructiveShellPatterns),
    ("Policy requires approval for writes", TestPolicyRequiresApprovalForWrite),
    ("Offline strict denies non-approved network shell", TestOfflineStrictDeniesNetworkShell),
    ("Offline strict allows approved gateway host with approval", TestOfflineStrictAllowsApprovedHostWithApproval),
    ("ReAct loop retries on non-json model output", TestLoopRetriesOnNonJsonOutput),
    ("ReAct loop recovers tool call from plain text action output", TestLoopRecoversToolCallFromPlainText),
    ("ReAct loop switches profile after invalid responses", TestLoopSwitchesProfileAfterInvalidResponses),
    ("ReAct loop stops after repeated invalid output", TestLoopStopsAfterRepeatedInvalidOutput),
    ("ReAct loop stops after repeated unknown tool output", TestLoopStopsAfterRepeatedUnknownToolOutput),
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

static Task TestPolicyDeniesSiblingPrefixBypass()
{
    var baseDir = Path.Combine(Path.GetTempPath(), "agent-policy-" + Guid.NewGuid().ToString("n"));
    var workspace = Path.Combine(baseDir, "repo");
    var sibling = Path.Combine(baseDir, "repo-evil");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(sibling);

    try
    {
        var config = new AgentConfig();
        var policy = new DefaultPolicyEngine(config);
        var outsidePath = Path.Combine(sibling, "leak.txt");
        using var doc = JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(outsidePath)}}}");
        var call = new ToolCall("fs_read", doc.RootElement.Clone(), "test");
        var context = new ToolContext(
            WorkspaceRoot: workspace,
            SessionId: "s1",
            ProfileName: "reasoning",
            Config: config,
            SearchService: new NullSearchService());

        var decision = policy.Evaluate(call, context);
        Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny decision for sibling-prefix outside workspace path.");
        return Task.CompletedTask;
    }
    finally
    {
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, true);
        }
    }
}

static Task TestPolicyDeniesExecShellCwdOutsideWorkspace()
{
    var baseDir = Path.Combine(Path.GetTempPath(), "agent-policy-shell-" + Guid.NewGuid().ToString("n"));
    var workspace = Path.Combine(baseDir, "repo");
    var sibling = Path.Combine(baseDir, "repo-out");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(sibling);

    try
    {
        var config = new AgentConfig();
        var policy = new DefaultPolicyEngine(config);
        using var doc = JsonDocument.Parse($"{{\"command\":\"pwd\",\"cwd\":{JsonSerializer.Serialize(sibling)}}}");
        var call = new ToolCall("exec_shell", doc.RootElement.Clone(), "test");
        var context = new ToolContext(
            WorkspaceRoot: workspace,
            SessionId: "s1",
            ProfileName: "reasoning",
            Config: config,
            SearchService: new NullSearchService());

        var decision = policy.Evaluate(call, context);
        Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny decision for exec_shell cwd outside workspace.");
        return Task.CompletedTask;
    }
    finally
    {
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, true);
        }
    }
}

static Task TestPolicyDeniesDestructiveShellPatterns()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);
    using var doc = JsonDocument.Parse("{\"command\":\"rm -rf .\"}");
    var call = new ToolCall("exec_shell", doc.RootElement.Clone(), "dangerous");
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService());

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny for destructive shell pattern.");
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

static Task TestOfflineStrictDeniesNetworkShell()
{
    var config = new AgentConfig
    {
        Api = new ApiConfig { BaseUrl = "https://gateway.company.local" },
        Safety = new SafetyConfig
        {
            OfflineStrictMode = true,
            AllowedNetworkHosts = new List<string> { "gateway.company.local" }
        }
    };

    var policy = new DefaultPolicyEngine(config);
    using var doc = JsonDocument.Parse("{\"command\":\"git push origin main\"}");
    var call = new ToolCall("exec_shell", doc.RootElement.Clone(), "push");
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService());

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny for network shell command in offline strict mode.");
    return Task.CompletedTask;
}

static Task TestOfflineStrictAllowsApprovedHostWithApproval()
{
    var config = new AgentConfig
    {
        Api = new ApiConfig { BaseUrl = "https://gateway.company.local" },
        Safety = new SafetyConfig
        {
            OfflineStrictMode = true,
            AllowedNetworkHosts = new List<string> { "gateway.company.local" }
        }
    };

    var policy = new DefaultPolicyEngine(config);
    using var doc = JsonDocument.Parse("{\"command\":\"curl https://gateway.company.local/health\"}");
    var call = new ToolCall("exec_shell", doc.RootElement.Clone(), "healthcheck");
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService());

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.RequireApproval, "Expected approval requirement for approved gateway host command.");
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

static async Task TestLoopRetriesOnNonJsonOutput()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("I will inspect files first.", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"inspect\",\"arguments\":{\"value\":\"ok\"}}", "fake"));
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

    var result = await loop.RunAsync(new AgentRunRequest("create a file with content", Path.GetTempPath(), "reasoning", 6), CancellationToken.None);
    Assert(result.Success, "Expected success after retrying invalid model output.");
    Assert(result.StepTrace.Count == 1, "Expected one executed tool step after invalid response retry.");
}

static async Task TestLoopRecoversToolCallFromPlainText()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-plain-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        var config = new AgentConfig();
        var responses = new Queue<ModelTurnResult>();
        responses.Enqueue(new ModelTurnResult("Action: fs_list\nArguments: {\"path\":\".\",\"recurse\":false,\"include_hidden\":false}", "fake"));
        responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

        var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
        var loop = new ReActAgentLoop(
            router,
            new ITool[] { new FsListTool() },
            new DefaultPolicyEngine(config),
            new AutoApproveService(true),
            new InMemoryEventStore(),
            new DefaultToolContextFactory(config, new NullSearchService()),
            config);

        var result = await loop.RunAsync(new AgentRunRequest("inspect project files", temp, "reasoning", 6), CancellationToken.None);
        Assert(result.Success, "Expected success after recovering tool call from plain text.");
        Assert(result.StepTrace.Count == 1, "Expected one tool step after plain-text recovery.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestLoopStopsAfterRepeatedInvalidOutput()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MaxSteps = 10,
            MaxInvalidModelResponses = 3,
            MaxConsecutiveFinalWithoutTools = 3,
            ToolTimeoutSeconds = 120,
            MaxOutputBytes = 64 * 1024,
            ModelMinOutputTokens = 256,
            ModelMaxOutputTokens = 4096,
            ModelMinTemperature = 0.0,
            ModelMaxTemperature = 0.7,
            LexicalSearchDefaultMaxResults = 20,
            RerankCandidateLimit = 12
        }
    };

    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("plain response 1", "fake"));
    responses.Enqueue(new ModelTurnResult("plain response 2", "fake"));
    responses.Enqueue(new ModelTurnResult("plain response 3", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("create file test.txt", Path.GetTempPath(), "reasoning", 10), CancellationToken.None);
    Assert(!result.Success, "Expected failure after repeated invalid model output.");
    Assert(result.FinalMessage.Contains("invalid model responses", StringComparison.OrdinalIgnoreCase), "Expected invalid-response stop message.");
}

static async Task TestLoopStopsAfterRepeatedUnknownToolOutput()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MaxSteps = 10,
            MaxInvalidModelResponses = 3,
            MaxConsecutiveFinalWithoutTools = 4,
            InvalidResponsesBeforeProfileSwitch = 10,
            FinalWithoutToolsBeforeProfileSwitch = 10,
            ToolTimeoutSeconds = 120,
            MaxOutputBytes = 64 * 1024,
            ModelMinOutputTokens = 256,
            ModelMaxOutputTokens = 4096,
            ModelMinTemperature = 0.0,
            ModelMaxTemperature = 0.7,
            LexicalSearchDefaultMaxResults = 20,
            RerankCandidateLimit = 12
        }
    };

    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"unknown_1\",\"reason\":\"x\",\"arguments\":{}}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"unknown_2\",\"reason\":\"x\",\"arguments\":{}}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"unknown_3\",\"reason\":\"x\",\"arguments\":{}}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("create file test.txt", Path.GetTempPath(), "reasoning", 10), CancellationToken.None);
    Assert(!result.Success, "Expected failure after repeated unknown-tool model output.");
    Assert(result.FinalMessage.Contains("invalid model decisions", StringComparison.OrdinalIgnoreCase), "Expected unknown-tool stop message.");
}

static async Task TestLoopSwitchesProfileAfterInvalidResponses()
{
    var config = new AgentConfig
    {
        Models = new Dictionary<string, ModelProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["reasoning"] = new() { Provider = "custom", Model = "deepseek", Temperature = 0.1, MaxTokens = 1200 },
            ["fallback"] = new() { Provider = "custom", Model = "glm", Temperature = 0.2, MaxTokens = 1200 }
        },
        Runtime = new RuntimeConfig
        {
            MaxSteps = 8,
            MaxInvalidModelResponses = 4,
            MaxConsecutiveFinalWithoutTools = 4,
            InvalidResponsesBeforeProfileSwitch = 2,
            FinalWithoutToolsBeforeProfileSwitch = 2,
            ToolTimeoutSeconds = 120,
            MaxOutputBytes = 64 * 1024,
            ModelMinOutputTokens = 256,
            ModelMaxOutputTokens = 4096,
            ModelMinTemperature = 0.0,
            ModelMaxTemperature = 0.7,
            LexicalSearchDefaultMaxResults = 20,
            RerankCandidateLimit = 12
        }
    };

    var reasoningResponses = new Queue<ModelTurnResult>();
    reasoningResponses.Enqueue(new ModelTurnResult("bad plain text", "reasoning"));
    reasoningResponses.Enqueue(new ModelTurnResult("another bad plain text", "reasoning"));

    var fallbackResponses = new Queue<ModelTurnResult>();
    fallbackResponses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"switch worked\",\"arguments\":{\"value\":\"ok\"}}", "fallback"));
    fallbackResponses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fallback"));

    var router = new MultiProfileModelRouter(new Dictionary<string, IModelClient>(StringComparer.OrdinalIgnoreCase)
    {
        ["reasoning"] = new FakeModelClient(reasoningResponses),
        ["fallback"] = new FakeModelClient(fallbackResponses)
    });

    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("create file a.txt", Path.GetTempPath(), "reasoning", 8), CancellationToken.None);
    Assert(result.Success, "Expected success after profile switch.");
    Assert(result.StepTrace.Count == 1, "Expected one tool call on fallback profile.");
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
