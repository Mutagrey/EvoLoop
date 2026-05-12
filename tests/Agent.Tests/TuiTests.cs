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
        ("TUI app cancels a running task", TestTuiAppCancelsRunningTask),
        ("TUI app dispatches plan command as read-only plan", TestTuiAppDispatchesPlanCommand),
        ("TUI app dispatches review command as read-only review", TestTuiAppDispatchesReviewCommand),
        ("TUI app navigates review diffs", TestTuiAppNavigatesReviewDiffs),
        ("TUI choice menu navigates and skips disabled items", TestTuiChoiceMenuNavigation),
        ("TUI choice menu confirms and cancels", TestTuiChoiceMenuConfirmCancel),
        ("TUI transcript scroll state tracks viewport offset", TestTuiTranscriptScrollState),
        ("TUI app reports unknown slash command", TestTuiUnknownSlashCommand),
        ("TUI app opens model picker and switches profiles", TestTuiModelPickerAndSwitch),
        ("TUI app shows model status and skills commands", TestTuiModelStatusAndSkillsCommands),
        ("TUI config command renders grouped settings", TestTuiConfigCommandRendersGroupedSettings),
        ("TUI config path command shows config paths", TestTuiConfigPathCommandShowsConfigPaths),
        ("TUI config open command uses attached opener", TestTuiConfigOpenCommandUsesAttachedOpener),
        ("TUI config reload updates runtime info", TestTuiConfigReloadUpdatesRuntimeInfo),
        ("TUI app inspects sessions storage memory and compacts context", TestTuiStorageSessionMemoryAndCompactCommands),
        ("TUI app prunes and archives storage logs", TestTuiStoragePruneAndArchiveCommands),
        ("TUI app clears transcript and rejects busy compact", TestTuiClearAndBusyCompactCommands),
        ("TUI runtime observer records agent events", TestTuiRuntimeObserverRecordsEvents),
        ("TUI app tracks model thinking state", TestTuiAppTracksModelThinkingState),
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

    var model = registry.Filter("/mo");
    Assert(model.Count == 2, "Expected /mo to match /model and /models.");
    Assert(model.Any(c => c.Name == "/model"), "Expected /mo to include /model.");
    Assert(model.Any(c => c.Name == "/models"), "Expected /mo to include /models.");

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
    Assert(runner.Calls[0].ApprovalMode == ApprovalPolicyMode.AutoEdit, "Expected configured default approval mode.");
}

static async Task TestTuiAppCancelsRunningTask()
{
    var runner = new BlockingTuiTaskRunner();
    var app = CreateTestTuiApp();
    app.AttachTaskRunner(runner);

    var run = app.SubmitAsync("long task", CancellationToken.None);
    await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

    Assert(app.IsTaskRunning, "Expected task to be running before cancellation.");
    Assert(app.CancelRunningTask(), "Expected cancellation request to be accepted.");

    var result = await run;
    Assert(result.IsError, "Expected cancelled task to return an error result.");
    Assert(result.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase), "Expected cancelled result message.");
    Assert(!app.IsTaskRunning, "Expected task running state to clear after cancellation.");
    Assert(app.Messages.Any(m => m.Content.Contains("cancelling current task", StringComparison.Ordinal)), "Expected cancelling status message.");
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

static async Task TestTuiAppNavigatesReviewDiffs()
{
    var runner = new CapturingTuiTaskRunner
    {
        Result = new AgentTaskRunResult(
            new AgentRunResult(true, "reviewed", 0, "review-session", Array.Empty<SessionStep>()),
            """
Snapshot workspace diff produced.
path: notes.txt
change_state: modified
snapshot_excerpt:
old
current_excerpt:
new
---
path: src/App.cs
change_state: created
snapshot_excerpt:
<empty>
current_excerpt:
class App { }
""")
    };
    var app = CreateTestTuiApp();
    app.AttachTaskRunner(runner);

    await app.SubmitAsync("/review", CancellationToken.None);

    Assert(app.Messages.Any(m => m.Content.Contains("Review diff ready: 2 files", StringComparison.Ordinal)), "Expected review diff ready status.");

    var files = app.Submit("/diff files");
    Assert(files.Handled, "Expected /diff files to be handled.");
    Assert(files.Message.Contains("notes.txt", StringComparison.Ordinal), "Expected diff file list.");
    Assert(files.Message.Contains("src/App.cs", StringComparison.Ordinal), "Expected second diff file.");

    var next = app.Submit("/diff next");
    Assert(next.Message.Contains("Diff 2/2: src/App.cs", StringComparison.Ordinal), "Expected /diff next to select second file.");

    var first = app.Submit("/diff 1");
    Assert(first.Message.Contains("Diff 1/2: notes.txt", StringComparison.Ordinal), "Expected /diff 1 to select first file.");
}

static Task TestTuiChoiceMenuNavigation()
{
    var state = new ChoiceMenuState(new[]
    {
        new ChoiceMenuItem("a", "A", "first"),
        new ChoiceMenuItem("b", "B", "disabled", IsDisabled: true),
        new ChoiceMenuItem("c", "C", "third"),
        new ChoiceMenuItem("d", "D", "fourth")
    }, "a");

    Assert(state.SelectedItem?.Id == "a", "Expected initial item.");
    state.MoveNext(2);
    Assert(state.SelectedItem?.Id == "c", "Expected navigation to skip disabled item.");
    state.MovePrevious(2);
    Assert(state.SelectedItem?.Id == "a", "Expected previous navigation to wrap past disabled item.");
    state.MoveEnd(2);
    Assert(state.SelectedItem?.Id == "d", "Expected End to select the last enabled item.");
    Assert(state.TopIndex == 2, "Expected menu scroll to keep the selected item visible.");
    state.MoveHome(2);
    Assert(state.SelectedItem?.Id == "a", "Expected Home to select the first enabled item.");
    return Task.CompletedTask;
}

static Task TestTuiChoiceMenuConfirmCancel()
{
    var state = new ChoiceMenuState(new[]
    {
        new ChoiceMenuItem("reject", "Reject", "default", IsCurrent: true),
        new ChoiceMenuItem("approve", "Approve", "allow")
    });

    Assert(state.Confirm() == "reject", "Expected confirm to return the selected item id.");
    state.MoveNext(2);
    Assert(state.Confirm() == "approve", "Expected confirm to follow selection.");
    Assert(ChoiceMenuState.Cancel() is null, "Expected cancel to return no selection.");
    return Task.CompletedTask;
}

static Task TestTuiTranscriptScrollState()
{
    var scroll = new TranscriptScrollState();

    Assert(scroll.GetStartLine(30, 10) == 20, "Expected bottom viewport start.");
    scroll.ScrollPageUp(30, 10);
    Assert(scroll.GetStartLine(30, 10) == 11, "Expected PageUp to move one page from bottom.");
    scroll.PreserveVisibleContentAfterAppend(30, 35, 10);
    Assert(scroll.GetStartLine(35, 10) == 11, "Expected appended output to preserve current visible line when scrolled up.");
    scroll.ScrollTop(30, 10);
    Assert(scroll.GetStartLine(30, 10) == 0, "Expected top viewport start.");
    scroll.ScrollPageDown(30, 10);
    Assert(scroll.GetStartLine(30, 10) == 9, "Expected PageDown to move toward bottom.");
    scroll.ScrollBottom();
    Assert(scroll.GetStartLine(30, 10) == 20, "Expected End/bottom to restore tail view.");
    scroll.RevealLineAtTop(5, 50, 10);
    Assert(scroll.GetStartLine(50, 10) == 5, "Expected long command output to reveal from its first line.");
    return Task.CompletedTask;
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

static async Task TestTuiModelPickerAndSwitch()
{
    TuiChoiceMenuRequest? captured = null;
    var app = CreateTestTuiApp(CreateModelRuntime());
    app.AttachChoicePrompt((request, _) =>
    {
        captured = request;
        return Task.FromResult<string?>("fast");
    });

    var picked = await app.SubmitAsync("/model", CancellationToken.None);

    Assert(picked.Handled, "Expected /model picker command to be handled.");
    Assert(captured is not null, "Expected /model to request a picker.");
    Assert(captured!.Items.Count == 2, "Expected model picker items.");
    Assert(captured.Items.Any(item => item.Id == "reasoning" && item.IsCurrent), "Expected active profile marker.");
    Assert(app.Runtime.Profile == "fast", "Expected selected profile to become active for the session.");
    Assert(app.Runtime.ModelId == "fast-model", "Expected selected profile details to update.");

    var direct = app.Submit("/model reasoning");
    Assert(direct.Handled, "Expected direct profile switch to be handled.");
    Assert(app.Runtime.Profile == "reasoning", "Expected direct switch to update active profile.");
}

static Task TestTuiModelStatusAndSkillsCommands()
{
    var app = CreateTestTuiApp();

    var model = app.Submit("/model status");
    Assert(model.Handled, "Expected /model to be handled.");
    Assert(model.Message.Contains("Model", StringComparison.Ordinal), "Expected model heading.");
    Assert(model.Message.Contains("active profile: reasoning", StringComparison.Ordinal), "Expected active model profile.");

    var models = app.Submit("/models");
    Assert(models.Handled, "Expected /models to be handled.");
    Assert(models.Message.Contains("Model profiles", StringComparison.Ordinal), "Expected model profiles heading.");
    Assert(models.Message.Contains("reasoning (active)", StringComparison.Ordinal), "Expected active profile marker.");

    var skills = app.Submit("/skills");
    Assert(skills.Handled, "Expected /skills to be handled.");
    Assert(skills.Message.Contains("Skills", StringComparison.Ordinal), "Expected skills heading.");
    Assert(skills.Message.Contains("No workspace skills", StringComparison.Ordinal), "Expected empty skills message.");
    return Task.CompletedTask;
}

static Task TestTuiConfigCommandRendersGroupedSettings()
{
    var app = CreateTestTuiApp();
    var result = app.Submit("/config");

    Assert(result.Handled, "Expected /config to be handled.");
    Assert(app.Messages.Last().Content.Contains("Connection", StringComparison.Ordinal), "Expected connection section.");
    Assert(app.Messages.Last().Content.Contains("Model Profiles", StringComparison.Ordinal), "Expected model profile section.");
    Assert(app.Messages.Last().Content.Contains("Tool Calling", StringComparison.Ordinal), "Expected tool-calling section.");
    Assert(app.Messages.Last().Content.Contains("Limits / Advanced", StringComparison.Ordinal), "Expected advanced limits section.");
    Assert(app.Messages.Last().Content.Contains("|-", StringComparison.Ordinal), "Expected tree-style config rendering.");
    return Task.CompletedTask;
}

static Task TestTuiConfigPathCommandShowsConfigPaths()
{
    var app = CreateTestTuiApp();
    var result = app.Submit("/config path");

    Assert(result.Handled, "Expected /config path to be handled.");
    Assert(app.Messages.Last().Content.Contains("loaded config:", StringComparison.Ordinal), "Expected loaded config path.");
    Assert(app.Messages.Last().Content.Contains("default config:", StringComparison.Ordinal), "Expected default config path.");
    return Task.CompletedTask;
}

static Task TestTuiConfigOpenCommandUsesAttachedOpener()
{
    var app = CreateTestTuiApp();
    var opener = new CapturingConfigFileOpener(new ConfigOpenResult(true, "opened"));
    app.AttachConfigFileOpener(opener);

    var result = app.Submit("/config open");

    Assert(result.Handled, "Expected /config open to be handled.");
    Assert(!result.IsError, "Expected successful open result.");
    Assert(opener.Paths.Count == 1, "Expected opener to be called once.");
    Assert(opener.Paths[0] == app.Runtime.ConfigPath, "Expected opener to receive current config path.");
    return Task.CompletedTask;
}

static async Task TestTuiConfigReloadUpdatesRuntimeInfo()
{
    var app = CreateTestTuiApp();
    app.AttachConfigReload(_ => Task.FromResult(app.Runtime with { ModeLabel = "full", ModelId = "reloaded-model" }));

    var result = await app.SubmitAsync("/config reload", CancellationToken.None);

    Assert(result.Handled, "Expected /config reload to be handled.");
    Assert(app.Runtime.ModeLabel == "full", "Expected runtime mode to update.");
    Assert(app.Runtime.ModelId == "reloaded-model", "Expected model id to update.");
}

static Task TestTuiStorageSessionMemoryAndCompactCommands()
{
    var workspace = CreateStorageWorkspace();
    try
    {
        WriteStorageSample(workspace, "aaaaaaaaaaaa1111", "completed", DateTimeOffset.UtcNow.AddMinutes(-2), "inspect files");
        var app = CreateTestTuiApp(CreateRuntimeForWorkspace(workspace));

        var sessions = app.Submit("/sessions");
        Assert(sessions.Handled, "Expected /sessions to be handled.");
        Assert(sessions.Message.Contains("aaaaaaaaaaaa", StringComparison.Ordinal), "Expected session id in list.");
        Assert(sessions.Message.Contains("inspect files", StringComparison.Ordinal), "Expected task in session list.");

        var session = app.Submit("/session aaaaaaaaaaaa");
        Assert(session.Handled, "Expected /session to be handled.");
        Assert(session.Message.Contains("fs_read", StringComparison.Ordinal), "Expected session steps.");
        Assert(session.Message.Contains("final: done", StringComparison.Ordinal), "Expected final answer event.");

        var storage = app.Submit("/storage");
        Assert(storage.Handled, "Expected /storage to be handled.");
        Assert(storage.Message.Contains("sessions.jsonl", StringComparison.Ordinal), "Expected sessions file stats.");
        Assert(storage.Message.Contains("snapshots", StringComparison.Ordinal), "Expected snapshot stats.");

        var memory = app.Submit("/memory");
        Assert(memory.Handled, "Expected /memory to be handled.");
        Assert(memory.Message.Contains("Workspace Memory", StringComparison.Ordinal), "Expected memory heading.");
        Assert(memory.Message.Contains("tools=fs_read", StringComparison.Ordinal), "Expected memory summary.");

        var compact = app.Submit("/compact");
        Assert(compact.Handled, "Expected /compact to be handled.");
        Assert(!compact.IsError, "Expected /compact to succeed.");
        var events = File.ReadAllText(Path.Combine(workspace, ".evoloop", "storage", "events.jsonl"));
        Assert(events.Contains("context_summary", StringComparison.Ordinal), "Expected compact event to be written.");
        var memoryRuns = File.ReadAllText(Path.Combine(workspace, ".evoloop", "storage", "memory-runs.jsonl"));
        Assert(memoryRuns.Contains("Manual TUI context compaction", StringComparison.Ordinal), "Expected compact summary to update memory.");
    }
    finally
    {
        Directory.Delete(workspace, true);
    }

    return Task.CompletedTask;
}

static Task TestTuiStoragePruneAndArchiveCommands()
{
    var workspace = CreateStorageWorkspace();
    try
    {
        WriteStorageSample(workspace, "oldsession0001", "completed", DateTimeOffset.UtcNow.AddMinutes(-10), "old task");
        WriteStorageSample(workspace, "newsession0002", "completed", DateTimeOffset.UtcNow.AddMinutes(-1), "new task");
        var app = CreateTestTuiApp(CreateRuntimeForWorkspace(workspace));

        var prune = app.Submit("/storage prune --keep 1");
        Assert(prune.Handled, "Expected /storage prune to be handled.");
        Assert(!prune.IsError, "Expected prune to succeed.");
        var sessionsAfterPrune = File.ReadAllText(Path.Combine(workspace, ".evoloop", "storage", "sessions.jsonl"));
        Assert(sessionsAfterPrune.Contains("newsession0002", StringComparison.Ordinal), "Expected latest session to remain.");
        Assert(!sessionsAfterPrune.Contains("oldsession0001", StringComparison.Ordinal), "Expected old session to be pruned.");

        var archive = app.Submit("/storage archive");
        Assert(archive.Handled, "Expected /storage archive to be handled.");
        Assert(!archive.IsError, "Expected archive to succeed.");
        var sessionsPath = Path.Combine(workspace, ".evoloop", "storage", "sessions.jsonl");
        Assert(File.ReadAllText(sessionsPath).Trim().Length == 0, "Expected sessions log to be rotated to an empty file.");
        var archiveRoot = Path.Combine(workspace, ".evoloop", "storage", "archive");
        Assert(Directory.Exists(archiveRoot), "Expected archive directory.");
        Assert(Directory.EnumerateFiles(archiveRoot, "sessions.jsonl", SearchOption.AllDirectories).Any(), "Expected archived sessions file.");
    }
    finally
    {
        Directory.Delete(workspace, true);
    }

    return Task.CompletedTask;
}

static async Task TestTuiClearAndBusyCompactCommands()
{
    var app = CreateTestTuiApp();
    var clear = app.Submit("/clear");
    Assert(clear.Handled, "Expected /clear to be handled.");
    Assert(app.Messages.Count == 1, "Expected transcript to contain only clear status.");
    Assert(app.Messages[0].Content.Contains("Transcript cleared", StringComparison.Ordinal), "Expected clear status.");

    var runner = new BlockingTuiTaskRunner();
    app.AttachTaskRunner(runner);
    var run = app.SubmitAsync("long task", CancellationToken.None);
    await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var compact = app.Submit("/compact");
    Assert(compact.IsError, "Expected compact during active task to be rejected.");
    Assert(compact.Message.Contains("Cannot compact while a task is running", StringComparison.Ordinal), "Expected busy compact message.");

    app.CancelRunningTask();
    await run;
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

static Task TestTuiAppTracksModelThinkingState()
{
    var app = CreateTestTuiApp();
    app.RecordRuntimeEvent(new AgentRunEvent(AgentRunEventType.ModelCallStarted, "Calling model", 1));

    Assert(app.IsModelThinking, "Expected model thinking state after model call starts.");

    app.RecordRuntimeEvent(new AgentRunEvent(AgentRunEventType.ToolExecutionStarted, "Running tool fs_read", 1, "fs_read"));

    Assert(!app.IsModelThinking, "Expected model thinking state to stop when tool execution starts.");
    return Task.CompletedTask;
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
        new Dictionary<string, string>
        {
            [ToolActivityMetadata.SuccessKey] = "true",
            [ToolActivityMetadata.KindKey] = "read",
            [ToolActivityMetadata.PathKey] = "src/Agent.Tui/TuiApp.cs",
            [ToolActivityMetadata.SummaryKey] = "Read src/Agent.Tui/TuiApp.cs"
        }));
    Assert(completed == "#2 read: src/Agent.Tui/TuiApp.cs (fs_read)", "Expected structured read completion line.");

    var search = TuiRuntimeEventFormatter.FormatText(new AgentRunEvent(
        AgentRunEventType.ToolExecutionCompleted,
        "Searched \"approval\"",
        3,
        "search_lexical",
        new Dictionary<string, string>
        {
            [ToolActivityMetadata.SuccessKey] = "true",
            [ToolActivityMetadata.KindKey] = "search",
            [ToolActivityMetadata.QueryKey] = "approval",
            [ToolActivityMetadata.SummaryKey] = "Searched \"approval\""
        }));
    Assert(search == "#3 search: \"approval\" (search_lexical)", "Expected structured search completion line.");

    var failed = TuiRuntimeEventFormatter.FormatText(new AgentRunEvent(
        AgentRunEventType.ToolExecutionCompleted,
        "File not found",
        4,
        "fs_read",
        new Dictionary<string, string>
        {
            [ToolActivityMetadata.SuccessKey] = "false",
            [ToolActivityMetadata.SummaryKey] = "fs_read failed: File not found"
        }));
    Assert(failed == "#4 failed: fs_read - fs_read failed: File not found", "Expected failed completion line.");
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
        TuiMessage.Status("runtime event"),
        TuiMessage.System("runtime event")
    }, 60);

    Assert(rendered.Contains("> hello", StringComparison.Ordinal), "Expected user message on the first line.");
    Assert(rendered.Contains("|- runtime event", StringComparison.Ordinal), "Expected status message to render as a tree item.");
    Assert(rendered.Contains("* runtime event", StringComparison.Ordinal), "Expected system message marker.");
    Assert(!rendered.Contains("status", StringComparison.OrdinalIgnoreCase), "Expected status role label to stay hidden.");
    Assert(rendered.Contains(':', StringComparison.Ordinal), "Expected timestamp in transcript.");
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
        var writeResult = await host.PatchService.WriteFileAsync(
            new FileWriteRequest("src/App.cs", "class App { }\n", true, null),
            toolContext,
            CancellationToken.None);
        Assert(writeResult.Success, "Expected second mutation to create multi-file snapshot evidence.");

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
        Assert(summary.Contains("Snapshot workspace diff produced.", StringComparison.Ordinal), "Expected workspace snapshot diff summary.");
        Assert(summary.Contains("unique_paths: 2", StringComparison.Ordinal), "Expected multi-file snapshot evidence.");
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

static TuiRuntimeInfo CreateModelRuntime()
{
    return new TuiRuntimeInfo(
        "/repo",
        "/repo",
        "reasoning",
        "full",
        "model ready",
        ApprovalPolicyMode.AutoEdit,
        TuiTheme.DefaultName,
        false,
        true,
        true)
    {
        ModelProfiles = new[] { "fast", "reasoning" },
        ModelProfileDetails = new[]
        {
            new TuiModelProfileInfo("fast", "custom", "fast-model", ToolCallingMode.JsonReActFallback),
            new TuiModelProfileInfo("reasoning", "custom", "reasoning-model", ToolCallingMode.NativeNonStreamingTools)
        },
        ModelProvider = "custom",
        ModelId = "reasoning-model",
        ToolCallingMode = ToolCallingMode.NativeNonStreamingTools
    };
}

static TuiApp CreateTestTuiApp(TuiRuntimeInfo? runtime = null)
{
    return new TuiApp(
        runtime ?? new TuiRuntimeInfo(
            "/repo",
            "/repo",
            "reasoning",
            "local-only degraded",
            "model unavailable",
            ApprovalPolicyMode.AutoEdit,
            TuiTheme.DefaultName,
            false,
            true,
            false),
        SlashCommandRegistry.CreateDefault());
}

static TuiRuntimeInfo CreateRuntimeForWorkspace(string workspace)
{
    return new TuiRuntimeInfo(
        workspace,
        workspace,
        "reasoning",
        "full",
        "model ready",
        ApprovalPolicyMode.AutoEdit,
        TuiTheme.DefaultName,
        false,
        true,
        true);
}

static string CreateStorageWorkspace()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-tui-storage-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(Path.Combine(workspace, ".evoloop", "storage"));
    return workspace;
}

static void WriteStorageSample(string workspace, string sessionId, string status, DateTimeOffset started, string task)
{
    static string J(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    var storage = Path.Combine(workspace, ".evoloop", "storage");
    Directory.CreateDirectory(storage);
    File.AppendAllText(
        Path.Combine(storage, "sessions.jsonl"),
        $"{{\"type\":\"session_start\",\"sessionId\":\"{sessionId}\",\"startedAtUtc\":\"{started:O}\",\"workspaceRoot\":{J(workspace)},\"profile\":\"reasoning\",\"task\":{J(task)}}}\n" +
        $"{{\"type\":\"session_end\",\"sessionId\":\"{sessionId}\",\"finalStatus\":\"{status}\",\"completedAtUtc\":\"{started.AddSeconds(30):O}\"}}\n");
    File.AppendAllText(
        Path.Combine(storage, "steps.jsonl"),
        $"{{\"SessionId\":\"{sessionId}\",\"StepNumber\":1,\"Action\":\"tool\",\"ToolName\":\"fs_read\",\"Reasoning\":\"read\",\"Success\":true,\"Output\":\"read ok\",\"TimestampUtc\":\"{started.AddSeconds(10):O}\",\"DurationMs\":5}}\n");
    File.AppendAllText(
        Path.Combine(storage, "events.jsonl"),
        $"{{\"SessionId\":\"{sessionId}\",\"EventType\":\"final_answer\",\"TimestampUtc\":\"{started.AddSeconds(20):O}\",\"Message\":\"done\",\"Success\":true}}\n");
    File.AppendAllText(
        Path.Combine(storage, "memory-runs.jsonl"),
        $"{{\"SessionId\":\"{sessionId}\",\"CompletedAtUtc\":\"{started.AddSeconds(30):O}\",\"Task\":{J(task)},\"Success\":true,\"FinalMessage\":\"done\",\"Summary\":\"tools=fs_read\",\"Highlights\":[\"read ok\"],\"RankScore\":0.0,\"ProjectId\":\"\",\"WorkspaceRoot\":{J(workspace)},\"WorkspaceRootHash\":\"\"}}\n");
}

internal sealed class CapturingTuiTaskRunner : ITuiTaskRunner
{
    public List<TuiTaskRunnerCall> Calls { get; } = new();
    public AgentTaskRunResult Result { get; set; } = new(
        new AgentRunResult(true, "done", 0, "test-session", Array.Empty<SessionStep>()),
        null);

    public Task<AgentTaskRunResult> RunAsync(
        string task,
        string profile,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IAgentRunObserver? observer,
        CancellationToken ct)
    {
        Calls.Add(new TuiTaskRunnerCall(task, profile, executionMode, approvalMode));
        return Task.FromResult(Result);
    }
}

internal sealed class BlockingTuiTaskRunner : ITuiTaskRunner
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<AgentTaskRunResult> RunAsync(
        string task,
        string profile,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IAgentRunObserver? observer,
        CancellationToken ct)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("Unreachable after cancellation.");
    }
}

internal sealed record TuiTaskRunnerCall(
    string Task,
    string Profile,
    AgentExecutionMode ExecutionMode,
    ApprovalPolicyMode ApprovalMode);

internal sealed class CapturingConfigFileOpener : IConfigFileOpener
{
    private readonly ConfigOpenResult _result;

    public CapturingConfigFileOpener(ConfigOpenResult result)
    {
        _result = result;
    }

    public List<string> Paths { get; } = new();

    public ConfigOpenResult Open(string path)
    {
        Paths.Add(path);
        return _result;
    }
}
}
