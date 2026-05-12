using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;
using static TestAssert;

internal static class ReActLoopContextTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("ReAct loop injects workspace memory into model context", TestLoopInjectsWorkspaceMemory),
        ("ReAct loop injects runtime capability context", TestLoopInjectsRuntimeCapabilityContext)
    };

static async Task TestLoopInjectsWorkspaceMemory()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MemoryEnabled = true
        }
    };

    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

    var client = new FakeModelClient(responses);
    var router = new FakeModelRouter(client, "fake");
    var memoryStore = new FakeMemoryStore(new WorkspaceMemoryContext("WORKSPACE MEMORY (test): previous fix in src/App.cs", 1));

    var loop = new ReActAgentLoop(
        router,
        Array.Empty<ITool>(),
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        memoryStore);

    var result = await loop.RunAsync(new AgentRunRequest("answer question", Path.GetTempPath(), "reasoning", 4), CancellationToken.None);
    Assert(result.Success, "Expected successful run.");
    Assert(client.SeenRequests.Count > 0, "Expected at least one model request.");
    Assert(client.SeenRequests[0].Messages.Any(m => m.Content.Contains("WORKSPACE MEMORY", StringComparison.OrdinalIgnoreCase)),
        "Expected memory context to be injected into first model request.");
    Assert(memoryStore.Saved.Count == 1, "Expected memory store to receive saved run.");
}

static async Task TestLoopInjectsRuntimeCapabilityContext()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MemoryEnabled = false
        }
    };

    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

    var client = new FakeModelClient(responses);
    var router = new FakeModelRouter(client, "fake");
    var capabilities = new RuntimeCapabilities(
        RuntimeOperatingMode.LocalOnlyDegraded,
        "Windows 11",
        "cmd.exe",
        true,
        true,
        false,
        false,
        false,
        false,
        false,
        false,
        "workspace storage available",
        "gateway not reachable");

    var loop = new ReActAgentLoop(
        router,
        Array.Empty<ITool>(),
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService(), capabilities),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("answer question", Path.GetTempPath(), "reasoning", 4), CancellationToken.None);
    Assert(result.Success, "Expected successful run.");
    Assert(client.SeenRequests.Count > 0, "Expected at least one model request.");
    Assert(client.SeenRequests[0].Messages.Any(m => m.Content.Contains("RUNTIME ENVIRONMENT", StringComparison.OrdinalIgnoreCase)),
        "Expected runtime capability context to be injected into first model request.");
}
}
