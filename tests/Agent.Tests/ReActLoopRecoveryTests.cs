using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;
using static TestAssert;

internal static class ReActLoopRecoveryTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("ReAct loop retries on non-json model output", TestLoopRetriesOnNonJsonOutput),
        ("ToolArgumentReader maps path aliases and nested input", TestToolArgumentReaderAliasAndNested),
        ("ReAct loop auto-repairs missing path from task context", TestLoopAutoRepairsMissingPathFromTask),
        ("ReAct loop falls back to fs_list when path is missing and unrecoverable", TestLoopFallsBackToFsListForMissingPath),
        ("ReAct loop repairs path from bullet-style output", TestLoopRepairsPathFromBulletStyleOutput),
        ("ReAct loop rejects tool call with missing required args", TestLoopRejectsMissingRequiredArgs),
        ("ReAct loop recovers tool call from plain text action output", TestLoopRecoversToolCallFromPlainText),
        ("ReAct loop parses tool_calls function variant", TestLoopParsesToolCallsFunctionVariant),
        ("ReAct loop bootstraps after final-without-tools", TestLoopBootstrapsAfterFinalWithoutTools)
    };

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

static Task TestToolArgumentReaderAliasAndNested()
{
    using var aliasDoc = JsonDocument.Parse("{\"filePath\":\"src/Program.cs\"}");
    var aliasPath = ToolArgumentReader.GetString(aliasDoc.RootElement, "path");
    Assert(aliasPath == "src/Program.cs", "Expected alias filePath to map to path.");

    using var nestedDoc = JsonDocument.Parse("{\"arguments\":{\"path\":\"src/App.cs\"}}");
    var nestedPath = ToolArgumentReader.GetString(nestedDoc.RootElement, "path");
    Assert(nestedPath == "src/App.cs", "Expected nested arguments.path to be resolved.");

    using var inputDoc = JsonDocument.Parse("{\"input\":\"{\\\"path\\\":\\\"src/Nested.cs\\\"}\"}");
    var inputPath = ToolArgumentReader.GetString(inputDoc.RootElement, "path");
    Assert(inputPath == "src/Nested.cs", "Expected JSON string input to be parsed for path.");

    using var commandDoc = JsonDocument.Parse("{\"input\":\"command: dotnet test\"}");
    var command = ToolArgumentReader.GetString(commandDoc.RootElement, "command");
    Assert(command == "dotnet test", "Expected command to be recovered from input text.");

    using var queryDoc = JsonDocument.Parse("{\"input\":\"search for \\\"ReActAgentLoop\\\"\"}");
    var query = ToolArgumentReader.GetString(queryDoc.RootElement, "query");
    Assert(query == "ReActAgentLoop", "Expected query to be recovered from input text.");

    using var commitDoc = JsonDocument.Parse("{\"input\":\"commit message: \\\"feat: stabilize parser\\\"\"}");
    var commitMessage = ToolArgumentReader.GetString(commitDoc.RootElement, "message");
    Assert(commitMessage == "feat: stabilize parser", "Expected commit message to be recovered from input text.");

    return Task.CompletedTask;
}

static async Task TestLoopAutoRepairsMissingPathFromTask()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-repair-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        await File.WriteAllTextAsync(Path.Combine(temp, "README.md"), "hello");

        var config = new AgentConfig();
        var responses = new Queue<ModelTurnResult>();
        responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"fs_read\",\"reason\":\"read target\",\"arguments\":{}}", "fake"));
        responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

        var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
        var loop = new ReActAgentLoop(
            router,
            new ITool[] { new FsReadTool() },
            new DefaultPolicyEngine(config),
            new AutoApproveService(true),
            new InMemoryEventStore(),
            new DefaultToolContextFactory(config, new NullSearchService()),
            config);

        var result = await loop.RunAsync(new AgentRunRequest("read README.md and summarize", temp, "reasoning", 6), CancellationToken.None);
        Assert(result.Success, "Expected success after auto-repairing missing path.");
        Assert(result.StepTrace.Count == 1, "Expected one fs_read step after auto-repair.");
        Assert(result.StepTrace[0].ToolName.Equals("fs_read", StringComparison.OrdinalIgnoreCase), "Expected fs_read tool execution.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestLoopFallsBackToFsListForMissingPath()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-fallback-path-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        await File.WriteAllTextAsync(Path.Combine(temp, "README.md"), "hello");

        var config = new AgentConfig();
        var responses = new Queue<ModelTurnResult>();
        responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"fs_read\",\"reason\":\"open target file\",\"arguments\":{}}", "fake"));
        responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

        var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
        var loop = new ReActAgentLoop(
            router,
            new ITool[] { new FsReadTool(), new FsListTool() },
            new DefaultPolicyEngine(config),
            new AutoApproveService(true),
            new InMemoryEventStore(),
            new DefaultToolContextFactory(config, new NullSearchService()),
            config);

        var result = await loop.RunAsync(new AgentRunRequest("inspect repository and continue", temp, "reasoning", 6), CancellationToken.None);
        Assert(result.Success, "Expected success after deterministic fs_list fallback for missing path.");
        Assert(result.StepTrace.Count == 1, "Expected exactly one fallback tool step.");
        Assert(result.StepTrace[0].ToolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase), "Expected fs_list fallback execution.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestLoopRepairsPathFromBulletStyleOutput()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-bullet-path-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        await File.WriteAllTextAsync(Path.Combine(temp, "README.md"), "hello");

        var config = new AgentConfig();
        var responses = new Queue<ModelTurnResult>();
        responses.Enqueue(new ModelTurnResult("tool: fs_read\n- path: README.md\nreason: inspect readme", "fake"));
        responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

        var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
        var loop = new ReActAgentLoop(
            router,
            new ITool[] { new FsReadTool() },
            new DefaultPolicyEngine(config),
            new AutoApproveService(true),
            new InMemoryEventStore(),
            new DefaultToolContextFactory(config, new NullSearchService()),
            config);

        var result = await loop.RunAsync(new AgentRunRequest("read README.md quickly", temp, "reasoning", 6), CancellationToken.None);
        Assert(result.Success, "Expected success when bullet-style path is recovered.");
        Assert(result.StepTrace.Count == 1, "Expected one fs_read step after recovery.");
        Assert(result.StepTrace[0].ToolName.Equals("fs_read", StringComparison.OrdinalIgnoreCase), "Expected fs_read tool execution.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestLoopRejectsMissingRequiredArgs()
{
    var config = new AgentConfig
    {
        Runtime = new RuntimeConfig
        {
            MaxSteps = 5,
            MaxInvalidModelResponses = 2,
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
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"fs_read\",\"reason\":\"read file\",\"arguments\":{}}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"fs_read\",\"reason\":\"read file\",\"arguments\":{}}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new FsReadTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("read file", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(!result.Success, "Expected failure after repeated missing required args.");
    Assert(result.FinalMessage.Contains("missing arguments", StringComparison.OrdinalIgnoreCase) ||
           result.FinalMessage.Contains("invalid model decisions", StringComparison.OrdinalIgnoreCase),
        "Expected missing-arguments validation failure to stop run.");
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

static async Task TestLoopParsesToolCallsFunctionVariant()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"tool_calls\":[{\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"value\\\":\\\"ok\\\"}\"}}]}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done\"}", "fake"));

    var router = new FakeModelRouter(new FakeModelClient(responses), "fake");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var result = await loop.RunAsync(new AgentRunRequest("run quick check", Path.GetTempPath(), "reasoning", 6), CancellationToken.None);
    Assert(result.Success, "Expected success when parser handles tool_calls function format.");
    Assert(result.StepTrace.Count == 1, "Expected one tool execution from tool_calls format.");
}

static async Task TestLoopBootstrapsAfterFinalWithoutTools()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-bootstrap-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        var config = new AgentConfig();
        var responses = new Queue<ModelTurnResult>();
        responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"done too early\"}", "fake"));
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

        var result = await loop.RunAsync(new AgentRunRequest("inspect project folder then finish", temp, "reasoning", 6), CancellationToken.None);
        Assert(result.Success, "Expected success after deterministic bootstrap tool call.");
        Assert(result.StepTrace.Count == 1, "Expected exactly one bootstrap tool call.");
        Assert(result.StepTrace[0].ToolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase), "Expected bootstrap fs_list tool.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}
}
