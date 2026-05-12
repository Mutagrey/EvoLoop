using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using Agent.Cli;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;

AssemblyLoadContext.Default.Resolving += ResolveFromOutput;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("CLI parser defaults to REPL and preserves explicit modes", TestCliParserModes),
    ("Policy denies outside workspace", TestPolicyDeniesOutsideWorkspace),
    ("Policy denies sibling path prefix bypass", TestPolicyDeniesSiblingPrefixBypass),
    ("Policy denies exec_shell cwd outside workspace", TestPolicyDeniesExecShellCwdOutsideWorkspace),
    ("Policy denies outside workspace path alias", TestPolicyDeniesOutsideWorkspacePathAlias),
    ("Policy denies destructive shell patterns", TestPolicyDeniesDestructiveShellPatterns),
    ("Policy reads nested shell command alias", TestPolicyReadsNestedShellCommandAlias),
    ("Policy requires approval for writes", TestPolicyRequiresApprovalForWrite),
    ("Policy denies protected path mutation", TestPolicyDeniesProtectedPathMutation),
    ("Offline strict denies non-approved network shell", TestOfflineStrictDeniesNetworkShell),
    ("Offline strict allows approved gateway host with approval", TestOfflineStrictAllowsApprovedHostWithApproval),
    ("Plan mode blocks mutating tools", TestPlanModeBlocksMutatingTools),
    ("Capability probe selects local-only degraded mode without model", TestCapabilityProbeSelectsLocalOnlyModeWithoutModel),
    ("Capability probe selects offline strict mode when model is ready", TestCapabilityProbeSelectsOfflineStrictModeWhenModelReady),
    ("ReAct loop retries on non-json model output", TestLoopRetriesOnNonJsonOutput),
    ("ToolArgumentReader maps path aliases and nested input", TestToolArgumentReaderAliasAndNested),
    ("ReAct loop auto-repairs missing path from task context", TestLoopAutoRepairsMissingPathFromTask),
    ("ReAct loop falls back to fs_list when path is missing and unrecoverable", TestLoopFallsBackToFsListForMissingPath),
    ("ReAct loop repairs path from bullet-style output", TestLoopRepairsPathFromBulletStyleOutput),
    ("ReAct loop rejects tool call with missing required args", TestLoopRejectsMissingRequiredArgs),
    ("ReAct loop recovers tool call from plain text action output", TestLoopRecoversToolCallFromPlainText),
    ("ReAct loop parses tool_calls function variant", TestLoopParsesToolCallsFunctionVariant),
    ("ReAct loop bootstraps after final-without-tools", TestLoopBootstrapsAfterFinalWithoutTools),
    ("ReAct loop switches profile after invalid responses", TestLoopSwitchesProfileAfterInvalidResponses),
    ("ReAct loop stops after repeated invalid output", TestLoopStopsAfterRepeatedInvalidOutput),
    ("ReAct loop stops after repeated unknown tool output", TestLoopStopsAfterRepeatedUnknownToolOutput),
    ("ReAct loop handles final response", TestLoopFinalResponse),
    ("ReAct loop accepts plain final text for non-tool task", TestLoopAcceptsPlainFinalTextForNonToolTask),
    ("ReAct loop executes tool then final", TestLoopToolThenFinal),
    ("Native non-streaming tool call executes and appends role tool result", TestNativeNonStreamingToolCallExecutes),
    ("Native multiple tool calls execute in order", TestNativeMultipleToolCallsExecuteInOrder),
    ("Streaming native tool call accumulates fragmented arguments", TestStreamingToolCallAccumulation),
    ("JSON-ReAct fallback normalizes tool call through adapter", TestJsonReActFallbackNormalizesToolCall),
    ("Plain text recovery parser recovers Action Arguments", TestPlainTextRecoveryParser),
    ("Tool error result becomes structured error message", TestToolErrorResultMessage),
    ("Fallback lexical search returns results", TestFallbackLexicalSearch),
    ("ReAct loop injects runtime capability context", TestLoopInjectsRuntimeCapabilityContext),
    ("ReAct loop injects workspace memory into model context", TestLoopInjectsWorkspaceMemory),
    ("Workspace memory store persists and loads context", TestWorkspaceMemoryStorePersistsAndLoadsContext),
    ("Workspace memory survives project directory move", TestWorkspaceMemorySurvivesDirectoryMove),
    ("Workspace memory filters noisy failed runs", TestWorkspaceMemoryFiltersNoisyFailedRuns),
    ("Patch service applies diff and undo", TestPatchServiceAppliesDiffAndUndo),
    ("Jsonl event log writes typed events", TestJsonlEventLogWritesTypedEvents)
};
tests.AddRange(SafetySearchTests.All);

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

static Task TestCliParserModes()
{
    var empty = CliArguments.Parse(Array.Empty<string>());
    Assert(empty.Mode == CliMode.Repl, "Expected bare CLI target to start REPL.");

    var repl = CliArguments.Parse(new[] { "repl", "--profile", "reasoning" });
    Assert(repl.Mode == CliMode.Repl, "Expected repl mode.");
    Assert(repl.Profile == "reasoning", "Expected profile option to be parsed.");

    var modelAlias = CliArguments.Parse(new[] { "run", "inspect", "--model", "fast" });
    Assert(modelAlias.Profile == "fast", "Expected --model to map to profile.");

    var run = CliArguments.Parse(new[] { "run", "inspect", "--offline-strict" });
    Assert(run.Mode == CliMode.Run, "Expected run mode.");
    Assert(run.Task == "inspect", "Expected run task to be parsed.");
    Assert(run.OfflineStrict, "Expected offline strict flag.");

    return Task.CompletedTask;
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
        SearchService: new NullSearchService(),
        Capabilities: RuntimeCapabilities.Default);

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
            SearchService: new NullSearchService(),
            Capabilities: RuntimeCapabilities.Default);

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
            SearchService: new NullSearchService(),
            Capabilities: RuntimeCapabilities.Default);

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

static Task TestPolicyDeniesOutsideWorkspacePathAlias()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);

    using var doc = JsonDocument.Parse("{\"filePath\":\"../secret.txt\"}");
    var call = new ToolCall("fs_read", doc.RootElement.Clone(), "alias path");
    var context = new ToolContext(
        WorkspaceRoot: "/tmp/workspace",
        SessionId: "s1",
        ProfileName: "reasoning",
        Config: config,
        SearchService: new NullSearchService(),
        Capabilities: RuntimeCapabilities.Default);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny decision for outside workspace path via alias.");
    return Task.CompletedTask;
}

static Task TestPolicyDeniesDestructiveShellPatterns()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);
    using var doc = JsonDocument.Parse("{\"command\":\"rm -rf .\"}");
    var call = new ToolCall("exec_shell", doc.RootElement.Clone(), "dangerous");
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService(), RuntimeCapabilities.Default);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected deny for destructive shell pattern.");
    return Task.CompletedTask;
}

static Task TestPolicyReadsNestedShellCommandAlias()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);
    using var doc = JsonDocument.Parse("{\"args\":{\"cmd\":\"git status\"}}");
    var call = new ToolCall("exec_shell", doc.RootElement.Clone(), "nested alias");
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService(), RuntimeCapabilities.Default);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind != PolicyDecisionKind.Deny, "Expected policy to read nested alias command and not treat it as an empty or denied command.");
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
        SearchService: new NullSearchService(),
        Capabilities: RuntimeCapabilities.Default);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.RequireApproval, "Expected approval requirement for fs_write.");
    return Task.CompletedTask;
}

static Task TestPolicyDeniesProtectedPathMutation()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);
    using var doc = JsonDocument.Parse("{\"path\":\".env\",\"content\":\"API_KEY=secret\"}");
    var call = new ToolCall("fs_write", doc.RootElement.Clone(), "write secret");
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService(), RuntimeCapabilities.Default);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Deny, "Expected protected path write to be denied.");
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
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService(), RuntimeCapabilities.Default);

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
    var context = new ToolContext("/tmp/workspace", "s1", "reasoning", config, new NullSearchService(), RuntimeCapabilities.Default);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.RequireApproval, "Expected approval requirement for approved gateway host command.");
    return Task.CompletedTask;
}

static async Task TestPlanModeBlocksMutatingTools()
{
    var responses = new Queue<ModelTurnResult>();
    responses.Enqueue(new ModelTurnResult("{\"type\":\"tool\",\"tool\":\"fs_write\",\"reason\":\"write plan output\",\"arguments\":{\"path\":\"plan.txt\",\"content\":\"x\"}}", "fake"));
    responses.Enqueue(new ModelTurnResult("{\"type\":\"final\",\"message\":\"plan complete\"}", "fake"));

    var client = new FakeModelClient(responses);
    var router = new FakeModelRouter(client, "fake-model");
    var config = new AgentConfig();
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new FsWriteTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config);

    var temp = Path.Combine(Path.GetTempPath(), "agent-plan-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);
    try
    {
        var result = await loop.RunAsync(new AgentRunRequest(
            "write a plan file",
            temp,
            "reasoning",
            AgentExecutionMode.Plan,
            ApprovalPolicyMode.ReadOnly,
            4), CancellationToken.None);

        Assert(result.Success, "Expected run to succeed after policy-blocked plan attempt followed by final response.");
        Assert(!File.Exists(Path.Combine(temp, "plan.txt")), "Plan mode must not create files.");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static Task TestCapabilityProbeSelectsLocalOnlyModeWithoutModel()
{
    var config = new AgentConfig();
    var mode = RuntimeCapabilityProbe.DetermineOperatingMode(config, modelReady: false);
    Assert(mode == RuntimeOperatingMode.LocalOnlyDegraded, "Expected local-only degraded mode when model is unavailable.");
    return Task.CompletedTask;
}

static Task TestCapabilityProbeSelectsOfflineStrictModeWhenModelReady()
{
    var config = new AgentConfig
    {
        Safety = new SafetyConfig
        {
            OfflineStrictMode = true
        }
    };

    var mode = RuntimeCapabilityProbe.DetermineOperatingMode(config, modelReady: true);
    Assert(mode == RuntimeOperatingMode.OfflineStrict, "Expected offline-strict mode when model is ready.");
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

static async Task TestNativeNonStreamingToolCallExecutes()
{
    var config = new AgentConfig
    {
        Models = new Dictionary<string, ModelProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["reasoning"] = new()
            {
                Provider = "openai",
                Model = "fake-native",
                ToolCallingMode = ToolCallingMode.NativeNonStreamingTools
            }
        }
    };

    var raw = """
              {
                "model":"fake-native",
                "choices":[
                  {
                    "message":{
                      "content":null,
                      "tool_calls":[
                        {
                          "id":"call_123",
                          "type":"function",
                          "function":{
                            "name":"echo",
                            "arguments":"{\"value\":\"ok\"}"
                          }
                        }
                      ]
                    }
                  }
                ],
                "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}
              }
              """;

    var responses = new Queue<ModelAdapterTurnResult>();
    responses.Enqueue(OpenAiCompatibleToolCallParser.ParseNonStreaming(raw, "fake-native", ToolCallingMode.NativeNonStreamingTools));
    responses.Enqueue(new ModelAdapterTurnResult(
        new AssistantMessage(new AssistantContentBlock[] { new TextBlock("native complete") }, "native complete", ToolCallingMode.NativeNonStreamingTools, AssistantMessageKind.Final),
        "fake-native",
        ToolCallingMode: ToolCallingMode.NativeNonStreamingTools));

    var adapter = new FakeModelAdapter(responses);
    var router = new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake-native");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        modelAdapterRouter: new FakeModelAdapterRouter(adapter));

    var result = await loop.RunAsync(new AgentRunRequest("run native tool", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(result.Success, "Expected native tool loop to complete.");
    Assert(result.StepTrace.Count == 1, "Expected one native tool execution.");
    Assert(adapter.SeenRequests.Count >= 2, "Expected second model request after tool result.");
    Assert(adapter.SeenRequests[1].Messages.Any(m =>
        m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
        m.ToolCallId == "call_123" &&
        m.Content.Contains("ok", StringComparison.OrdinalIgnoreCase)),
        "Expected role=tool result with matching tool_call_id to be appended for native mode.");
}

static async Task TestNativeMultipleToolCallsExecuteInOrder()
{
    var config = new AgentConfig
    {
        Models = new Dictionary<string, ModelProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["reasoning"] = new()
            {
                Provider = "openai",
                Model = "fake-native",
                ToolCallingMode = ToolCallingMode.NativeNonStreamingTools
            }
        }
    };

    var raw = """
              {
                "model":"fake-native",
                "choices":[
                  {
                    "message":{
                      "content":null,
                      "tool_calls":[
                        {"id":"call_a","type":"function","function":{"name":"echo","arguments":"{\"value\":\"a\"}"}},
                        {"id":"call_b","type":"function","function":{"name":"echo","arguments":"{\"value\":\"b\"}"}}
                      ]
                    }
                  }
                ]
              }
              """;

    var responses = new Queue<ModelAdapterTurnResult>();
    responses.Enqueue(OpenAiCompatibleToolCallParser.ParseNonStreaming(raw, "fake-native", ToolCallingMode.NativeNonStreamingTools));
    responses.Enqueue(new ModelAdapterTurnResult(
        new AssistantMessage(new AssistantContentBlock[] { new TextBlock("complete") }, "complete", ToolCallingMode.NativeNonStreamingTools, AssistantMessageKind.Final),
        "fake-native",
        ToolCallingMode: ToolCallingMode.NativeNonStreamingTools));

    var adapter = new FakeModelAdapter(responses);
    var router = new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake-native");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        modelAdapterRouter: new FakeModelAdapterRouter(adapter));

    var result = await loop.RunAsync(new AgentRunRequest("run two native tools", Path.GetTempPath(), "reasoning", 6), CancellationToken.None);
    Assert(result.Success, "Expected multi-tool native turn to complete.");
    Assert(result.StepTrace.Count == 2, "Expected both native tool calls to execute.");
    Assert(result.StepTrace[0].Output.Contains("a", StringComparison.OrdinalIgnoreCase), "Expected first tool result first.");
    Assert(result.StepTrace[1].Output.Contains("b", StringComparison.OrdinalIgnoreCase), "Expected second tool result second.");
    Assert(adapter.SeenRequests.Last().Messages.Count(m => m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase)) == 2,
        "Expected both native tool results to be appended before the final model turn.");
}

static Task TestStreamingToolCallAccumulation()
{
    var stream = string.Join('\n', new[]
    {
        "data: {\"model\":\"fake-stream\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_stream\",\"type\":\"function\",\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"val\"}}]}}]}",
        "data: {\"model\":\"fake-stream\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ue\\\":\\\"ok\\\"}\"}}]}}]}",
        "data: [DONE]"
    });

    var parsed = OpenAiCompatibleToolCallParser.ParseStreaming(stream, "fake-stream");
    var call = parsed.AssistantMessage.ToolCalls.Single();
    Assert(call.Id.Value == "call_stream", "Expected streaming tool call id to be preserved.");
    Assert(call.Name.Value == "echo", "Expected streaming tool name to be preserved.");
    Assert(ToolArgumentReader.GetString(call.Arguments, "value") == "ok", "Expected fragmented arguments to reconstruct valid JSON.");
    return Task.CompletedTask;
}

static async Task TestJsonReActFallbackNormalizesToolCall()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelAdapterTurnResult>();
    responses.Enqueue(new ModelAdapterTurnResult(
        JsonReActResponseParser.Parse(
            "{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"test\",\"arguments\":{\"value\":\"ok\"}}",
            new[] { "echo" },
            allowPlainTextRecovery: false),
        "fake-json"));
    responses.Enqueue(new ModelAdapterTurnResult(
        new AssistantMessage(new AssistantContentBlock[] { new TextBlock("json complete") }, "{\"type\":\"final\",\"message\":\"json complete\"}", ToolCallingMode.JsonReActFallback, AssistantMessageKind.Final),
        "fake-json"));

    var adapter = new FakeModelAdapter(responses);
    var router = new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake-json");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        modelAdapterRouter: new FakeModelAdapterRouter(adapter));

    var result = await loop.RunAsync(new AgentRunRequest("run json fallback", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(result.Success, "Expected JSON-ReAct fallback adapter result to complete.");
    Assert(result.StepTrace.Count == 1, "Expected one tool execution from normalized JSON fallback.");
}

static Task TestPlainTextRecoveryParser()
{
    var parsed = PlainTextRecoveryParser.Parse(
        "Action: echo\nArguments: {\"value\":\"ok\"}",
        new[] { "echo" });

    var call = parsed.ToolCalls.Single();
    Assert(call.Name.Value == "echo", "Expected plain-text recovery to find tool name.");
    Assert(ToolArgumentReader.GetString(call.Arguments, "value") == "ok", "Expected plain-text recovery to parse arguments.");
    Assert(parsed.ToolCallingMode == ToolCallingMode.PlainTextRecoveryFallback, "Expected plain-text recovery mode marker.");
    return Task.CompletedTask;
}

static Task TestToolErrorResultMessage()
{
    using var doc = JsonDocument.Parse("{\"value\":\"bad\"}");
    var call = new ToolCallBlock(new ToolCallId("call_err"), new ToolName("echo"), doc.RootElement.Clone(), "test");
    var message = ToolResultMessage.FromToolResult(call, new ToolResult(false, "failed", StdErr: "boom"));
    Assert(message.IsError, "Expected failed tool result to be marked as error.");
    Assert(message.ToolCallId.Value == "call_err", "Expected error result to preserve tool call id.");
    Assert(message.Content.StdErr == "boom", "Expected stderr to be preserved in structured content.");
    return Task.CompletedTask;
}

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

static async Task TestWorkspaceMemoryStorePersistsAndLoadsContext()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-memory-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        var config = new AgentConfig();
        var memory = new WorkspaceMemoryStore(temp, config);

        var steps = new List<SessionStep>
        {
            new(
                SessionId: "s1",
                StepNumber: 1,
                Action: "tool",
                ToolName: "fs_write",
                Reasoning: "create file",
                Success: true,
                Output: "Wrote file: src/App.cs",
                TimestampUtc: DateTimeOffset.UtcNow,
                DurationMs: 10,
                Error: null)
        };

        await memory.SaveRunAsync(new WorkspaceMemoryRecord(
            WorkspaceRoot: temp,
            SessionId: "s1",
            Task: "create app file",
            Success: true,
            FinalMessage: "done",
            Steps: steps,
            CompletedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);

        var loaded = await memory.LoadContextAsync(temp, "edit app file", CancellationToken.None);
        Assert(loaded.EntriesUsed > 0, "Expected memory context entries.");
        Assert(loaded.Content.Contains("create app file", StringComparison.OrdinalIgnoreCase), "Expected memory content to include prior task.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestWorkspaceMemorySurvivesDirectoryMove()
{
    var baseDir = Path.Combine(Path.GetTempPath(), "agent-tests-memory-move-" + Guid.NewGuid().ToString("n"));
    var original = Path.Combine(baseDir, "repo-original");
    var moved = Path.Combine(baseDir, "repo-moved");
    Directory.CreateDirectory(original);

    try
    {
        var config = new AgentConfig();
        var store = new WorkspaceMemoryStore(original, config);
        var steps = new List<SessionStep>
        {
            new(
                SessionId: "s-move",
                StepNumber: 1,
                Action: "tool",
                ToolName: "fs_write",
                Reasoning: "create file",
                Success: true,
                Output: "Wrote file: src/Move.cs",
                TimestampUtc: DateTimeOffset.UtcNow,
                DurationMs: 10,
                Error: null)
        };

        await store.SaveRunAsync(new WorkspaceMemoryRecord(
            WorkspaceRoot: original,
            SessionId: "s-move",
            Task: "create move file",
            Success: true,
            FinalMessage: "done",
            Steps: steps,
            CompletedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);

        Directory.Move(original, moved);

        var movedStore = new WorkspaceMemoryStore(moved, config);
        var loaded = await movedStore.LoadContextAsync(moved, "edit move file", CancellationToken.None);
        Assert(loaded.EntriesUsed > 0, "Expected memory entries to remain available after directory move.");

        var identityPath = Path.Combine(moved, ".evoloop", "project.identity.json");
        Assert(File.Exists(identityPath), "Expected project identity file to exist after move.");
    }
    finally
    {
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, true);
        }
    }
}

static async Task TestWorkspaceMemoryFiltersNoisyFailedRuns()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-tests-memory-filter-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(temp);

    try
    {
        var config = new AgentConfig();
        var memory = new WorkspaceMemoryStore(temp, config);

        await memory.SaveRunAsync(new WorkspaceMemoryRecord(
            WorkspaceRoot: temp,
            SessionId: "failed-noise",
            Task: "download package and install dependency",
            Success: false,
            FinalMessage: "Fatal error: gateway unavailable",
            Steps: Array.Empty<SessionStep>(),
            CompletedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);

        var successSteps = new List<SessionStep>
        {
            new(
                SessionId: "success-1",
                StepNumber: 1,
                Action: "tool",
                ToolName: "fs_write",
                Reasoning: "write config",
                Success: true,
                Output: "Wrote file: config/appsettings.json",
                TimestampUtc: DateTimeOffset.UtcNow,
                DurationMs: 12,
                Error: null)
        };

        await memory.SaveRunAsync(new WorkspaceMemoryRecord(
            WorkspaceRoot: temp,
            SessionId: "success-1",
            Task: "update config file",
            Success: true,
            FinalMessage: "updated config",
            Steps: successSteps,
            CompletedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);

        var loaded = await memory.LoadContextAsync(temp, "edit config values", CancellationToken.None);
        Assert(!loaded.Content.Contains("gateway unavailable", StringComparison.OrdinalIgnoreCase),
            "Expected noisy failed run to be filtered from injected memory.");
        Assert(loaded.Content.Contains("update config file", StringComparison.OrdinalIgnoreCase),
            "Expected useful successful run to remain in memory context.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestPatchServiceAppliesDiffAndUndo()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-patch-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(workspace);

    try
    {
        var filePath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\n");

        var service = new WorkspacePatchService();
        var context = new ToolContext(
            workspace,
            "s1",
            "reasoning",
            AgentExecutionMode.Run,
            ApprovalPolicyMode.WorkspaceWrite,
            new AgentConfig(),
            new NullSearchService(),
            RuntimeCapabilities.Default,
            service,
            NullEventLog.Instance);

        var patch = string.Join('\n', new[]
        {
            "--- a/notes.txt",
            "+++ b/notes.txt",
            "@@ -1,2 +1,2 @@",
            " alpha",
            "-beta",
            "+gamma"
        });

        var patchResult = await service.ApplyPatchAsync(new FilePatchRequest("notes.txt", patch, null, null), context, CancellationToken.None);
        Assert(patchResult.Success, "Expected built-in patch service to apply unified diff.");
        var updated = await File.ReadAllTextAsync(filePath);
        Assert(updated.Contains("gamma", StringComparison.Ordinal), "Expected patched content to be written.");

        var undoResult = await service.UndoLastAsync(workspace, CancellationToken.None);
        Assert(undoResult.Success, "Expected undo to restore previous file state.");
        var restored = await File.ReadAllTextAsync(filePath);
        Assert(restored.Contains("beta", StringComparison.Ordinal), "Expected undo to restore original content.");
    }
    finally
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}

static async Task TestJsonlEventLogWritesTypedEvents()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-event-log-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(workspace);

    try
    {
        var log = new JsonlEventLog(workspace);
        await log.AppendAsync(new AgentEventRecord(
            "s1",
            "tool_call",
            DateTimeOffset.UtcNow,
            "run test",
            "echo",
            true,
            new Dictionary<string, string> { ["step"] = "1" }), CancellationToken.None);

        var path = Path.Combine(workspace, ".evoloop", "storage", "events.jsonl");
        Assert(File.Exists(path), "Expected JSONL event log file to exist.");
        var content = await File.ReadAllTextAsync(path);
        Assert(content.Contains("\"EventType\":\"tool_call\"", StringComparison.Ordinal), "Expected typed event payload in JSONL log.");
        Assert(content.Contains("\"ToolName\":\"echo\"", StringComparison.Ordinal), "Expected tool name in JSONL log.");
    }
    finally
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
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
