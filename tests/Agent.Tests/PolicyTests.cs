using System.Text.Json;
using Agent.Core;
using Agent.Tools;
using static TestAssert;

internal static class PolicyTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("Policy denies outside workspace", TestPolicyDeniesOutsideWorkspace),
        ("Policy denies sibling path prefix bypass", TestPolicyDeniesSiblingPrefixBypass),
        ("Policy denies exec_shell cwd outside workspace", TestPolicyDeniesExecShellCwdOutsideWorkspace),
        ("Policy denies outside workspace path alias", TestPolicyDeniesOutsideWorkspacePathAlias),
        ("Policy denies destructive shell patterns", TestPolicyDeniesDestructiveShellPatterns),
        ("Policy reads nested shell command alias", TestPolicyReadsNestedShellCommandAlias),
        ("Policy requires approval for writes", TestPolicyRequiresApprovalForWrite),
        ("Policy auto-edit allows write without approval", TestPolicyAutoEditAllowsWriteWithoutApproval),
        ("Policy auto-edit requires approval for delete", TestPolicyAutoEditRequiresApprovalForDelete),
        ("Policy denies protected path mutation", TestPolicyDeniesProtectedPathMutation),
        ("Offline strict denies non-approved network shell", TestOfflineStrictDeniesNetworkShell),
        ("Offline strict allows approved gateway host with approval", TestOfflineStrictAllowsApprovedHostWithApproval),
        ("Plan mode blocks mutating tools", TestPlanModeBlocksMutatingTools),
        ("Review mode blocks mutating tools and shell", TestReviewModeBlocksMutatingToolsAndShell)
    };

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
        "/tmp/workspace",
        "s1",
        "reasoning",
        AgentExecutionMode.Run,
        ApprovalPolicyMode.WorkspaceWrite,
        config,
        new NullSearchService(),
        RuntimeCapabilities.Default,
        NullPatchService.Instance,
        NullEventLog.Instance);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.RequireApproval, "Expected approval requirement for fs_write.");
    return Task.CompletedTask;
}

static Task TestPolicyAutoEditAllowsWriteWithoutApproval()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(config);

    using var doc = JsonDocument.Parse("{\"path\":\"file.txt\",\"content\":\"x\"}");
    var call = new ToolCall("fs_write", doc.RootElement.Clone(), "write");
    var context = new ToolContext(
        "/tmp/workspace",
        "s1",
        "reasoning",
        AgentExecutionMode.Run,
        ApprovalPolicyMode.AutoEdit,
        config,
        new NullSearchService(),
        RuntimeCapabilities.Default,
        NullPatchService.Instance,
        NullEventLog.Instance);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.Allow, "Expected AutoEdit to allow normal file writes.");
    return Task.CompletedTask;
}

static Task TestPolicyAutoEditRequiresApprovalForDelete()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(new ITool[] { new FsDeleteTool() }, config);

    using var doc = JsonDocument.Parse("{\"path\":\"file.txt\"}");
    var call = new ToolCall("fs_delete", doc.RootElement.Clone(), "delete");
    var context = new ToolContext(
        "/tmp/workspace",
        "s1",
        "reasoning",
        AgentExecutionMode.Run,
        ApprovalPolicyMode.AutoEdit,
        config,
        new NullSearchService(),
        RuntimeCapabilities.Default,
        NullPatchService.Instance,
        NullEventLog.Instance);

    var decision = policy.Evaluate(call, context);
    Assert(decision.Kind == PolicyDecisionKind.RequireApproval, "Expected AutoEdit to require approval for delete.");
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

static Task TestReviewModeBlocksMutatingToolsAndShell()
{
    var config = new AgentConfig();
    var policy = new DefaultPolicyEngine(new ITool[] { new FsWriteTool(), new ExecShellTool() }, config);
    var context = new ToolContext(
        "/tmp/workspace",
        "s1",
        "reasoning",
        AgentExecutionMode.Review,
        ApprovalPolicyMode.AutoEdit,
        config,
        new NullSearchService(),
        RuntimeCapabilities.Default,
        new WorkspacePatchService(),
        NullEventLog.Instance);

    using var writeDoc = JsonDocument.Parse("{\"path\":\"review.txt\",\"content\":\"x\"}");
    var writeDecision = policy.Evaluate(new ToolCall("fs_write", writeDoc.RootElement.Clone(), "write"), context);
    Assert(writeDecision.Kind == PolicyDecisionKind.Deny, "Expected review mode to deny workspace mutation.");
    Assert(writeDecision.Reason.Contains("Review mode", StringComparison.Ordinal), "Expected review-mode denial reason.");

    using var shellDoc = JsonDocument.Parse("{\"command\":\"git status\"}");
    var shellDecision = policy.Evaluate(new ToolCall("exec_shell", shellDoc.RootElement.Clone(), "shell"), context);
    Assert(shellDecision.Kind == PolicyDecisionKind.Deny, "Expected review mode to deny shell execution.");
    Assert(shellDecision.Reason.Contains("Review mode", StringComparison.Ordinal), "Expected review-mode shell denial reason.");
    return Task.CompletedTask;
}
}
