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
        ("ReAct loop injects runtime capability context", TestLoopInjectsRuntimeCapabilityContext),
        ("Prompt builder injects workspace system prompt files", TestPromptBuilderInjectsWorkspacePromptFiles),
        ("Context builder indexes workspace prompt templates", TestContextBuilderIndexesPromptTemplates)
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

static async Task TestPromptBuilderInjectsWorkspacePromptFiles()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-prompt-files-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(Path.Combine(workspace, ".evoloop"));

    try
    {
        await File.WriteAllTextAsync(Path.Combine(workspace, ".evoloop", "SYSTEM.md"), "Prefer compact answers.");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".evoloop", "APPEND_SYSTEM.md"), "Mention exact changed files.");
        var config = new AgentConfig();
        var context = new ToolContext(
            workspace,
            "s1",
            "reasoning",
            config,
            new NullSearchService(),
            RuntimeCapabilities.Default);

        var prompt = new DefaultPromptBuilder().BuildSystemPrompt(Array.Empty<ITool>(), context);
        Assert(prompt.Contains("WORKSPACE SYSTEM", StringComparison.Ordinal), "Expected workspace SYSTEM.md section.");
        Assert(prompt.Contains("Prefer compact answers", StringComparison.Ordinal), "Expected SYSTEM.md content.");
        Assert(prompt.Contains("WORKSPACE APPEND_SYSTEM", StringComparison.Ordinal), "Expected workspace APPEND_SYSTEM.md section.");
        Assert(prompt.Contains("Mention exact changed files", StringComparison.Ordinal), "Expected APPEND_SYSTEM.md content.");
        Assert(prompt.Contains("Return EXACTLY one JSON object", StringComparison.Ordinal), "Expected core harness contract to remain in code prompt.");
    }
    finally
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}

static async Task TestContextBuilderIndexesPromptTemplates()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-prompt-templates-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(Path.Combine(workspace, ".evoloop", "prompts"));

    try
    {
        await File.WriteAllTextAsync(Path.Combine(workspace, ".evoloop", "prompts", "review.md"), "# Review Template\nUse review style.");
        var config = new AgentConfig { Runtime = new RuntimeConfig { MemoryEnabled = false } };
        var context = new ToolContext(
            workspace,
            "s1",
            "reasoning",
            config,
            new NullSearchService(),
            RuntimeCapabilities.Default);

        var messages = await new DefaultContextBuilder().BuildInitialMessagesAsync(
            new AgentRunRequest("inspect", workspace, "reasoning", 4),
            context,
            NullWorkspaceMemoryStore.Instance,
            CancellationToken.None);

        Assert(messages.Any(m => m.Content.Contains("WORKSPACE PROMPT TEMPLATES", StringComparison.Ordinal)), "Expected prompt template index.");
        Assert(messages.Any(m => m.Content.Contains(".evoloop/prompts/review.md", StringComparison.Ordinal)), "Expected template path.");
    }
    finally
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
}
