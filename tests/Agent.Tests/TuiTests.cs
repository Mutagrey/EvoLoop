using Agent.Core;
using Agent.Hosting;
using Agent.Tools;
using Agent.Tui;
using static TestAssert;

internal static class TuiTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("TUI slash commands filter and render help", TestTuiSlashCommands),
        ("TUI parser accepts theme options", TestTuiParserThemeOptions),
        ("TUI theme resolves default and no-color variants", TestTuiThemeResolution),
        ("TUI app rejects task when runtime is not attached", TestTuiAppRejectsTaskWithoutRuntime),
        ("TUI app dispatches plain input as run", TestTuiAppDispatchesPlainInputAsRun),
        ("TUI app dispatches plan command as read-only plan", TestTuiAppDispatchesPlanCommand),
        ("TUI app dispatches review command as read-only review", TestTuiAppDispatchesReviewCommand),
        ("TUI app reports unknown slash command", TestTuiUnknownSlashCommand),
        ("TUI runtime observer records agent events", TestTuiRuntimeObserverRecordsEvents),
        ("TUI runtime formatter renders compact tool events", TestTuiRuntimeFormatterToolEvents),
        ("TUI runtime formatter renders approval and completion events", TestTuiRuntimeFormatterApprovalAndCompletionEvents),
        ("TUI approval service records default rejection", TestTuiApprovalServiceRecordsDefaultRejection),
        ("TUI approval service uses attached prompt", TestTuiApprovalServiceUsesAttachedPrompt),
        ("TUI approval formatter renders patch diff", TestTuiApprovalFormatterPatchDiff),
        ("TUI approval formatter renders write content preview", TestTuiApprovalFormatterWriteContentPreview),
        ("TUI transcript renderer formats roles", TestTuiTranscriptRenderer),
        ("Agent task runner uses local snapshot review fallback without model", TestAgentTaskRunnerLocalSnapshotReviewFallback)
    };

static Task TestTuiSlashCommands()
{
    var registry = SlashCommandRegistry.CreateDefault();
    var filtered = registry.Filter("/h");
    Assert(filtered.Count == 1, "Expected /h to match one command.");
    Assert(filtered[0].Name == "/help", "Expected /h to match /help.");

    var help = registry.Execute("/help");
    Assert(help.Handled, "Expected /help to be handled.");
    Assert(!help.ExitRequested, "Expected /help to keep the TUI open.");
    Assert(help.Message.Contains("/exit", StringComparison.Ordinal), "Expected help to list /exit.");
    Assert(help.Message.Contains("shared agent runtime", StringComparison.OrdinalIgnoreCase), "Expected help to describe runtime-backed input.");
    return Task.CompletedTask;
}

static Task TestTuiParserThemeOptions()
{
    var themed = TuiArguments.Parse(new[] { "--theme", "mono" });
    Assert(themed.Theme == "mono", "Expected --theme to be parsed.");

    var noColor = TuiArguments.Parse(new[] { "--no-color", "--theme", "claude-dark" });
    Assert(noColor.NoColor, "Expected --no-color to be parsed.");
    Assert(noColor.Theme == "claude-dark", "Expected explicit theme to remain visible even when no-color is set.");
    return Task.CompletedTask;
}

static Task TestTuiThemeResolution()
{
    var dark = TuiTheme.Resolve(null, false);
    Assert(dark.Name == TuiTheme.DefaultName, "Expected default theme to resolve to claude-dark.");

    var mono = TuiTheme.Resolve("claude-dark", true);
    Assert(mono.Name == TuiTheme.NoColorName, "Expected no-color to force mono theme.");
    return Task.CompletedTask;
}

static Task TestTuiAppRejectsTaskWithoutRuntime()
{
    var app = CreateTestTuiApp();
    var result = app.Submit("inspect project");

    Assert(!result.Handled, "Expected normal input without attached runtime to be rejected.");
    Assert(!result.ExitRequested, "Expected normal input to keep TUI open.");
    Assert(app.Messages.Any(m => m.Role == TuiMessageRole.User && m.Content == "inspect project"), "Expected user message in transcript.");
    Assert(app.Messages.Any(m => m.Content.Contains("runtime is not attached", StringComparison.Ordinal)), "Expected missing runtime notice.");
    return Task.CompletedTask;
}

static async Task TestTuiAppDispatchesPlainInputAsRun()
{
    var runner = new CapturingTuiTaskRunner();
    var app = CreateTestTuiApp();
    app.AttachTaskRunner(runner);

    var result = await app.SubmitAsync("inspect project", CancellationToken.None);

    Assert(result.Handled, "Expected plain input to run through attached runtime.");
    Assert(runner.Calls.Count == 1, "Expected one runtime call.");
    Assert(runner.Calls[0].Task == "inspect project", "Expected task text to be passed through.");
    Assert(runner.Calls[0].ExecutionMode == AgentExecutionMode.Run, "Expected run mode.");
    Assert(runner.Calls[0].ApprovalMode == ApprovalPolicyMode.WorkspaceWrite, "Expected configured default approval mode.");
}

static async Task TestTuiAppDispatchesPlanCommand()
{
    var runner = new CapturingTuiTaskRunner();
    var app = CreateTestTuiApp();
    app.AttachTaskRunner(runner);

    var result = await app.SubmitAsync("/plan inspect architecture", CancellationToken.None);

    Assert(result.Handled, "Expected /plan to run through attached runtime.");
    Assert(runner.Calls.Count == 1, "Expected one runtime call.");
    Assert(runner.Calls[0].Task == "inspect architecture", "Expected plan task text without command prefix.");
    Assert(runner.Calls[0].ExecutionMode == AgentExecutionMode.Plan, "Expected plan mode.");
    Assert(runner.Calls[0].ApprovalMode == ApprovalPolicyMode.ReadOnly, "Expected read-only approval mode.");
}

static async Task TestTuiAppDispatchesReviewCommand()
{
    var runner = new CapturingTuiTaskRunner();
    var app = CreateTestTuiApp();
    app.AttachTaskRunner(runner);

    var result = await app.SubmitAsync("/review safety", CancellationToken.None);

    Assert(result.Handled, "Expected /review to run through attached runtime.");
    Assert(runner.Calls.Count == 1, "Expected one runtime call.");
    Assert(runner.Calls[0].Task.Contains("Review current workspace changes", StringComparison.Ordinal), "Expected canonical review task.");
    Assert(runner.Calls[0].Task.Contains("Focus: safety", StringComparison.Ordinal), "Expected review focus.");
    Assert(runner.Calls[0].ExecutionMode == AgentExecutionMode.Review, "Expected review mode.");
    Assert(runner.Calls[0].ApprovalMode == ApprovalPolicyMode.ReadOnly, "Expected read-only approval mode.");
}

static Task TestTuiUnknownSlashCommand()
{
    var app = CreateTestTuiApp();
    var result = app.Submit("/missing");

    Assert(!result.Handled, "Expected unknown command to be unhandled.");
    Assert(result.IsError, "Expected unknown command to return an error.");
    Assert(app.Messages.Last().Role == TuiMessageRole.Error, "Expected unknown command to append an error message.");
    Assert(app.Messages.Last().Content.Contains("Unknown command", StringComparison.Ordinal), "Expected unknown command details.");
    return Task.CompletedTask;
}

static async Task TestTuiRuntimeObserverRecordsEvents()
{
    var app = CreateTestTuiApp();
    var observer = new TuiRuntimeObserver(app);

    await observer.OnEventAsync(new AgentRunEvent(
        AgentRunEventType.ToolExecutionCompleted,
        "read src/Agent.Tui/TuiApp.cs",
        2,
        "fs_read"), CancellationToken.None);

    Assert(app.StatusLine.Contains("ok: fs_read", StringComparison.Ordinal), "Expected status line to reflect compact runtime event.");
    Assert(app.Messages.Last().Role == TuiMessageRole.Status, "Expected runtime event to append a status message.");
    Assert(app.Messages.Last().Content.Contains("fs_read", StringComparison.Ordinal), "Expected runtime event to include tool name.");
}

static Task TestTuiRuntimeFormatterToolEvents()
{
    var running = TuiRuntimeEventFormatter.FormatText(new AgentRunEvent(
        AgentRunEventType.ToolExecutionStarted,
        "Running tool fs_read",
        2,
        "fs_read"));
    Assert(running == "#2 run: fs_read", "Expected compact tool start line.");

    var completed = TuiRuntimeEventFormatter.FormatText(new AgentRunEvent(
        AgentRunEventType.ToolExecutionCompleted,
        "read src/Agent.Tui/TuiApp.cs",
        2,
        "fs_read",
        new Dictionary<string, string> { [ToolActivityMetadata.SuccessKey] = "true" }));
    Assert(completed == "#2 ok: fs_read - read src/Agent.Tui/TuiApp.cs", "Expected compact tool completion line.");
    return Task.CompletedTask;
}

static Task TestTuiRuntimeFormatterApprovalAndCompletionEvents()
{
    var approval = TuiRuntimeEventFormatter.Format(new AgentRunEvent(
        AgentRunEventType.ApprovalRequired,
        "write requested",
        3,
        "fs_write"));
    Assert(approval.Role == TuiMessageRole.Status, "Expected approval request to render as status.");
    Assert(approval.Content == "#3 approval required: fs_write - write requested", "Expected compact approval request.");

    var rejected = TuiRuntimeEventFormatter.Format(new AgentRunEvent(
        AgentRunEventType.ApprovalRejected,
        "User rejected tool execution.",
        3,
        "fs_write"));
    Assert(rejected.Role == TuiMessageRole.Error, "Expected rejected approval to render as error.");
    Assert(rejected.Content == "#3 rejected: fs_write", "Expected compact approval rejection.");

    var done = TuiRuntimeEventFormatter.Format(new AgentRunEvent(
        AgentRunEventType.SessionCompleted,
        "Task completed"));
    Assert(done.Role == TuiMessageRole.Assistant, "Expected session completion to render as assistant.");
    Assert(done.Content == "done: Task completed", "Expected compact session completion.");
    return Task.CompletedTask;
}

static async Task TestTuiApprovalServiceRecordsDefaultRejection()
{
    var app = CreateTestTuiApp();
    var approval = new TuiApprovalService(app);

    var approved = await approval.RequestApprovalAsync(new ApprovalRequest(
        "fs_write",
        "write requested",
        "{\"path\":\"x\"}"), CancellationToken.None);

    Assert(!approved, "Expected default TUI approval service to reject until an interactive prompt is wired.");
    Assert(app.Messages.Any(m => m.Content.Contains("approval required: fs_write", StringComparison.Ordinal)), "Expected approval request in transcript.");
    Assert(app.Messages.Last().Content.Contains("rejected: fs_write", StringComparison.Ordinal), "Expected rejection result in transcript.");
}

static async Task TestTuiApprovalServiceUsesAttachedPrompt()
{
    var app = CreateTestTuiApp();
    app.AttachApprovalPrompt((_, _) => Task.FromResult(true));
    var approval = new TuiApprovalService(app, app.RequestApprovalAsync);

    var approved = await approval.RequestApprovalAsync(new ApprovalRequest(
        "fs_write",
        "write requested",
        "{\"path\":\"x\"}"), CancellationToken.None);

    Assert(approved, "Expected attached TUI approval prompt to approve.");
    Assert(app.Messages.Last().Content.Contains("approved: fs_write", StringComparison.Ordinal), "Expected approval result in transcript.");
}

static Task TestTuiApprovalFormatterPatchDiff()
{
    var preview = TuiApprovalRequestFormatter.FormatForDialog(new ApprovalRequest(
        "fs_patch",
        "update file",
        "{\"path\":\"src/App.cs\",\"unified_diff\":\"--- a/src/App.cs\\n+++ b/src/App.cs\\n@@ -1 +1 @@\\n-old\\n+new\"}"));

    Assert(preview.Contains("Tool: fs_patch", StringComparison.Ordinal), "Expected tool name.");
    Assert(preview.Contains("Path: src/App.cs", StringComparison.Ordinal), "Expected path.");
    Assert(preview.Contains("Diff:", StringComparison.Ordinal), "Expected diff heading.");
    Assert(preview.Contains("+new", StringComparison.Ordinal), "Expected diff content.");
    return Task.CompletedTask;
}

static Task TestTuiApprovalFormatterWriteContentPreview()
{
    var preview = TuiApprovalRequestFormatter.FormatForDialog(new ApprovalRequest(
        "fs_write",
        "write file",
        "{\"path\":\"notes.txt\",\"content\":\"line one\\nline two\"}"));

    Assert(preview.Contains("Path: notes.txt", StringComparison.Ordinal), "Expected path.");
    Assert(preview.Contains("Content preview:", StringComparison.Ordinal), "Expected content preview heading.");
    Assert(preview.Contains("line two", StringComparison.Ordinal), "Expected content preview.");

    var fallback = TuiApprovalRequestFormatter.FormatForDialog(new ApprovalRequest(
        "custom",
        "raw",
        "not json"));
    Assert(fallback.Contains("Arguments:", StringComparison.Ordinal), "Expected raw arguments heading.");
    Assert(fallback.Contains("not json", StringComparison.Ordinal), "Expected raw arguments.");
    return Task.CompletedTask;
}

static Task TestTuiTranscriptRenderer()
{
    var rendered = TranscriptRenderer.Render(new[]
    {
        TuiMessage.User("hello"),
        TuiMessage.System("runtime event")
    }, 60);

    Assert(rendered.Contains("[user] hello", StringComparison.Ordinal), "Expected user role prefix.");
    Assert(rendered.Contains("[system] runtime event", StringComparison.Ordinal), "Expected system role prefix.");
    return Task.CompletedTask;
}

static async Task TestAgentTaskRunnerLocalSnapshotReviewFallback()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-review-fallback-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(workspace);

    try
    {
        var filePath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\n");

        var capabilities = new RuntimeCapabilities(
            RuntimeOperatingMode.LocalOnlyDegraded,
            "test",
            "/bin/sh",
            true,
            true,
            false,
            false,
            false,
            true,
            false,
            false,
            "workspace storage available",
            "gateway not reachable");
        var context = new AgentRuntimeContext(workspace, workspace, new AgentConfig(), capabilities);
        using var host = AgentExecutionHost.Create(context, new AutoApproveService(true));
        var patch = string.Join('\n', new[]
        {
            "--- a/notes.txt",
            "+++ b/notes.txt",
            "@@ -1,2 +1,2 @@",
            " alpha",
            "-beta",
            "+gamma"
        });

        var toolContext = new ToolContext(
            workspace,
            "s1",
            "reasoning",
            AgentExecutionMode.Run,
            ApprovalPolicyMode.WorkspaceWrite,
            new AgentConfig(),
            new NullSearchService(),
            capabilities,
            host.PatchService,
            NullEventLog.Instance);
        var patchResult = await host.PatchService.ApplyPatchAsync(new FilePatchRequest("notes.txt", patch, null, null), toolContext, CancellationToken.None);
        Assert(patchResult.Success, "Expected patch setup to create snapshot evidence.");

        var runner = new AgentTaskRunner(host, context);
        var review = await runner.RunAsync(
            AgentTaskRunner.BuildReviewTask(null),
            "reasoning",
            AgentExecutionMode.Review,
            ApprovalPolicyMode.ReadOnly,
            null,
            CancellationToken.None);

        Assert(review.Result.Success, "Expected local review fallback to succeed without model.");
        Assert(review.Result.SessionId == "local-review", "Expected local review session id.");
        Assert(review.LocalReviewSummary is not null, "Expected local review summary.");
        var summary = review.LocalReviewSummary ?? string.Empty;
        Assert(summary.Contains("snapshot_hash", StringComparison.Ordinal), "Expected snapshot diff evidence.");
        Assert(summary.Contains("current_hash", StringComparison.Ordinal), "Expected current file evidence.");

        var run = await runner.RunAsync(
            "do work",
            "reasoning",
            AgentExecutionMode.Run,
            ApprovalPolicyMode.WorkspaceWrite,
            null,
            CancellationToken.None);
        Assert(!run.Result.Success, "Expected non-review run to remain blocked without model.");
        Assert(run.LocalReviewSummary is null, "Expected no local summary for blocked run mode.");
    }
    finally
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}

static TuiApp CreateTestTuiApp()
{
    return new TuiApp(
        new TuiRuntimeInfo(
            "/repo",
            "/repo",
            "reasoning",
            "local-only degraded",
            "model unavailable",
            ApprovalPolicyMode.WorkspaceWrite,
            TuiTheme.DefaultName,
            false,
            true,
            false),
        SlashCommandRegistry.CreateDefault());
}

internal sealed class CapturingTuiTaskRunner : ITuiTaskRunner
{
    public List<TuiTaskRunnerCall> Calls { get; } = new();

    public Task<AgentTaskRunResult> RunAsync(
        string task,
        string profile,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IAgentRunObserver? observer,
        CancellationToken ct)
    {
        Calls.Add(new TuiTaskRunnerCall(task, profile, executionMode, approvalMode));
        return Task.FromResult(new AgentTaskRunResult(
            new AgentRunResult(true, "done", 0, "test-session", Array.Empty<SessionStep>()),
            null));
    }
}

internal sealed record TuiTaskRunnerCall(
    string Task,
    string Profile,
    AgentExecutionMode ExecutionMode,
    ApprovalPolicyMode ApprovalMode);
}
