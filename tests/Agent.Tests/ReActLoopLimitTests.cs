using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;
using static TestAssert;

internal static class ReActLoopLimitTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("ReAct loop switches profile after invalid responses", TestLoopSwitchesProfileAfterInvalidResponses),
        ("ReAct loop stops after repeated invalid output", TestLoopStopsAfterRepeatedInvalidOutput),
        ("ReAct loop stops after repeated unknown tool output", TestLoopStopsAfterRepeatedUnknownToolOutput),
        ("ReAct loop stops after repeated final-without-tools output", TestLoopStopsAfterRepeatedFinalWithoutToolsOutput),
        ("ReAct loop stops at max steps", TestLoopStopsAtMaxSteps)
    };

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
            ProfileFallbackOrder = new List<string> { "fallback" },
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

static async Task TestLoopStopsAfterRepeatedInvalidOutput()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MaxSteps = 10,
            MaxInvalidModelResponses = 3,
            MaxConsecutiveFinalWithoutTools = 3,
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

static async Task TestLoopStopsAfterRepeatedFinalWithoutToolsOutput()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MaxSteps = 6,
            MaxConsecutiveFinalWithoutTools = 2,
            FinalWithoutToolsBeforeProfileSwitch = 10
        }
    };

    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done too early 1\"}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done too early 2\"}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("create file test.txt", Path.GetTempPath(), "reasoning", 6), CancellationToken.None);
    Assert(!result.Success, "Expected failure after repeated final-without-tools output.");
    Assert(result.FinalMessage.Contains("final-only replies", StringComparison.OrdinalIgnoreCase), "Expected final-without-tools stop message.");
    Assert(result.StepTrace.Count == 0, "Expected no tool steps before final-without-tools stop.");
}

static async Task TestLoopStopsAtMaxSteps()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"step 1\",\"arguments\":{\"value\":\"one\"}}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"step 2\",\"arguments\":{\"value\":\"two\"}}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("run repeated checks", Path.GetTempPath(), "reasoning", 2), CancellationToken.None);
    Assert(!result.Success, "Expected max-step exhaustion to fail.");
    Assert(result.FinalMessage.Contains("Reached max steps (2)", StringComparison.OrdinalIgnoreCase), "Expected max-step stop message.");
    Assert(result.StepTrace.Count == 2, "Expected each allowed step to execute before max-step stop.");
}
}
