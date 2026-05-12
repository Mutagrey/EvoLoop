using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;
using static TestAssert;

internal static class ReActLoopBasicTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("ReAct loop returns clarify response", TestLoopClarifyResponse),
        ("ReAct loop handles final response", TestLoopFinalResponse),
        ("ReAct loop accepts plain final text for non-tool task", TestLoopAcceptsPlainFinalTextForNonToolTask),
        ("ReAct loop executes tool then final", TestLoopToolThenFinal)
    };

static async Task TestLoopClarifyResponse()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"clarify\",\"message\":\"Which file should I inspect?\"}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("inspect a file", Path.GetTempPath(), "reasoning", 4), CancellationToken.None);
    Assert(!result.Success, "Expected clarify response to end without success.");
    Assert(result.FinalMessage.Contains("Which file", StringComparison.OrdinalIgnoreCase), "Expected clarify message to be returned.");
    Assert(result.StepTrace.Count == 0, "Expected no tool steps for clarify response.");
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

static async Task TestLoopAcceptsPlainFinalTextForNonToolTask()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("Here is the concise answer to your question.", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        Array.Empty<ITool>(),
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("what is the purpose of this tool?", Path.GetTempPath(), "reasoning", 4), CancellationToken.None);
    Assert(result.Success, "Expected plain text to be accepted as final for non-tool task.");
    Assert(result.FinalMessage.Contains("concise answer", StringComparison.OrdinalIgnoreCase), "Expected recovered plain final message.");
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
}
