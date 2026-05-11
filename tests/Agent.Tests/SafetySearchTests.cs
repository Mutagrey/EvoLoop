using Agent.Core;
using Agent.Providers;
using Agent.Tools;

internal static class SafetySearchTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("Path safety denies symlink write escape", TestPathSafetyDeniesSymlinkWriteEscape),
        ("Path safety denies symlink patch escape", TestPathSafetyDeniesSymlinkPatchEscape),
        ("Fallback search skips generated and storage paths", TestFallbackSearchSkipsGeneratedAndStoragePaths)
    };

    private static async Task TestPathSafetyDeniesSymlinkWriteEscape()
    {
        var (workspace, outside, cleanup) = CreateSymlinkWorkspace();
        if (workspace is null || outside is null || cleanup is null)
        {
            return;
        }

        try
        {
            var config = new AgentConfig();
            var context = new ToolContext(
                workspace,
                "s1",
                "reasoning",
                AgentExecutionMode.Run,
                ApprovalPolicyMode.AutoEdit,
                config,
                new NullSearchService(),
                RuntimeCapabilities.Default,
                new WorkspacePatchService(),
                NullEventLog.Instance);

            var service = new WorkspacePatchService();
            var denied = false;
            try
            {
                await service.WriteFileAsync(new FileWriteRequest("escape/new.txt", "x", true, null), context, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                denied = true;
            }

            Assert(denied, "Expected symlink write escape to be denied.");
            Assert(!File.Exists(Path.Combine(outside, "new.txt")), "Write escape must not create outside file.");
        }
        finally
        {
            cleanup();
        }
    }

    private static async Task TestPathSafetyDeniesSymlinkPatchEscape()
    {
        var (workspace, outside, cleanup) = CreateSymlinkWorkspace();
        if (workspace is null || outside is null || cleanup is null)
        {
            return;
        }

        try
        {
            var config = new AgentConfig();
            var context = new ToolContext(
                workspace,
                "s1",
                "reasoning",
                AgentExecutionMode.Run,
                ApprovalPolicyMode.AutoEdit,
                config,
                new NullSearchService(),
                RuntimeCapabilities.Default,
                new WorkspacePatchService(),
                NullEventLog.Instance);

            var service = new WorkspacePatchService();
            var denied = false;
            try
            {
                await service.ApplyPatchAsync(new FilePatchRequest("escape/new.txt", null, "x", null), context, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                denied = true;
            }

            Assert(denied, "Expected symlink patch escape to be denied.");
            Assert(!File.Exists(Path.Combine(outside, "new.txt")), "Patch escape must not create outside file.");
        }
        finally
        {
            cleanup();
        }
    }

    private static async Task TestFallbackSearchSkipsGeneratedAndStoragePaths()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "agent-search-skip-" + Guid.NewGuid().ToString("n"));
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Directory.CreateDirectory(workspace);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "visible.txt"), "needle visible");
            Directory.CreateDirectory(Path.Combine(workspace, "artifacts"));
            Directory.CreateDirectory(Path.Combine(workspace, ".evoloop", "storage"));
            Directory.CreateDirectory(Path.Combine(workspace, "bin"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "artifacts", "skip.txt"), "needle artifact");
            await File.WriteAllTextAsync(Path.Combine(workspace, ".evoloop", "storage", "skip.txt"), "needle storage");
            await File.WriteAllTextAsync(Path.Combine(workspace, "bin", "skip.txt"), "needle bin");
            await File.WriteAllTextAsync(Path.Combine(workspace, "payload.exe"), "needle binary");

            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var service = new HybridSearchService(new DisabledModelClientRouter("disabled"), new AgentConfig(), workspace);
            var hits = await service.LexicalAsync(new SearchQuery(workspace, "needle", 20), CancellationToken.None);
            var paths = hits.Select(hit => hit.FilePath.Replace('\\', '/')).ToArray();

            Assert(paths.Contains("visible.txt"), "Expected fallback search to include normal source file.");
            Assert(!paths.Any(path => path.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase)), "Fallback search must skip artifacts.");
            Assert(!paths.Any(path => path.StartsWith(".evoloop/storage/", StringComparison.OrdinalIgnoreCase)), "Fallback search must skip workspace storage.");
            Assert(!paths.Any(path => path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)), "Fallback search must skip bin.");
            Assert(!paths.Contains("payload.exe"), "Fallback search must skip binary files.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, true);
            }
        }
    }

    private static (string? Workspace, string? Outside, Action? Cleanup) CreateSymlinkWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-symlink-" + Guid.NewGuid().ToString("n"));
        var workspace = Path.Combine(root, "repo");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(workspace, "escape"), outside);
            return (workspace, outside, () =>
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            });
        }
        catch
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            return (null, null, null);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
