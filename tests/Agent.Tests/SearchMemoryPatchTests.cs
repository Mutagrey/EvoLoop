using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;
using static TestAssert;

internal static class SearchMemoryPatchTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("Fallback lexical search returns results", TestFallbackLexicalSearch),
        ("Workspace memory store persists and loads context", TestWorkspaceMemoryStorePersistsAndLoadsContext),
        ("Workspace memory survives project directory move", TestWorkspaceMemorySurvivesDirectoryMove),
        ("Workspace memory filters noisy failed runs", TestWorkspaceMemoryFiltersNoisyFailedRuns),
        ("Patch service applies diff and undo", TestPatchServiceAppliesDiffAndUndo),
        ("Patch service fails explicitly when snapshot storage is unavailable", TestPatchServiceFailsWhenSnapshotStorageUnavailable),
        ("Patch service undo validates snapshot before replacing directory", TestPatchServiceUndoValidatesSnapshotBeforeReplacingDirectory),
        ("Jsonl event log writes typed events", TestJsonlEventLogWritesTypedEvents)
    };

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

static async Task TestPatchServiceFailsWhenSnapshotStorageUnavailable()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-patch-storage-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(workspace);

    try
    {
        await File.WriteAllTextAsync(Path.Combine(workspace, ".evoloop"), "not a directory");

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

        var result = await service.WriteFileAsync(
            new FileWriteRequest("notes.txt", "content", true, null),
            context,
            CancellationToken.None);

        Assert(!result.Success, "Expected write to fail when snapshot storage is unavailable.");
        Assert(result.Message.Contains("Snapshot storage unavailable", StringComparison.Ordinal), "Expected explicit snapshot storage error.");
        Assert(!File.Exists(Path.Combine(workspace, "notes.txt")), "Expected mutation to be skipped when snapshot capture fails.");
    }
    finally
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}

static async Task TestPatchServiceUndoValidatesSnapshotBeforeReplacingDirectory()
{
    var workspace = Path.Combine(Path.GetTempPath(), "agent-undo-failure-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(workspace);

    try
    {
        var targetDir = Path.Combine(workspace, "data");
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "before.txt"), "before");

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

        var deleteResult = await service.DeleteAsync(new FileDeleteRequest("data", true), context, CancellationToken.None);
        Assert(deleteResult.Success, "Expected directory delete to capture undo snapshot.");

        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "current.txt"), "current");

        var manifestPath = Path.Combine(workspace, ".evoloop", "storage", "snapshots", "last-mutation.json");
        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var snapshotPath = manifestDoc.RootElement.GetProperty("SnapshotPath").GetString();
        Assert(!string.IsNullOrWhiteSpace(snapshotPath), "Expected snapshot path in manifest.");
        Directory.Delete(snapshotPath!, true);

        var undoResult = await service.UndoLastAsync(workspace, CancellationToken.None);

        Assert(!undoResult.Success, "Expected undo to fail when snapshot directory is missing.");
        Assert(undoResult.Message.Contains("snapshot directory is missing", StringComparison.Ordinal), "Expected explicit missing snapshot error.");
        Assert(File.Exists(Path.Combine(targetDir, "current.txt")), "Expected current target directory to remain untouched after undo failure.");
        Assert(File.Exists(manifestPath), "Expected manifest to remain after undo failure.");
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
}
