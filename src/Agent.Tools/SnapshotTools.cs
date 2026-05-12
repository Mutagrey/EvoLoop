using System.Security.Cryptography;
using System.Text;
using Agent.Core;

namespace Agent.Tools;

public sealed class WorkspaceUndoTool : ITool
{
    public string Name => "workspace_undo";
    public ToolMetadata Metadata => new(ToolRiskLevel.High, ToolCategory.Review, true, new[] { "workspace_write" });

    public ToolSchema Schema => new(
        "Undo the last workspace file mutation captured by snapshot storage.",
        Array.Empty<string>(),
        new Dictionary<string, string>());

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => context.PatchService.UndoLastAsync(context.WorkspaceRoot, ct);
}

public sealed class WorkspaceSnapshotDiffTool : ITool
{
    public string Name => "workspace_snapshot_diff";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Review, false, Array.Empty<string>());

    public ToolSchema Schema => new(
        "Show the diff/status between the current workspace path and the latest mutation snapshot.",
        Array.Empty<string>(),
        new Dictionary<string, string>());

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var manifestPath = MutationSnapshotManifestStore.GetManifestPath(context.WorkspaceRoot);
        IReadOnlyList<MutationSnapshotManifest> manifests;
        try
        {
            manifests = await MutationSnapshotManifestStore.ReadHistoryAsync(context.WorkspaceRoot, ct);
            if (manifests.Count == 0 && File.Exists(manifestPath))
            {
                var latest = await MutationSnapshotManifestStore.ReadAsync(context.WorkspaceRoot, ct);
                manifests = latest is null
                    ? Array.Empty<MutationSnapshotManifest>()
                    : new[] { latest };
            }
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"Failed to read snapshot manifest: {ex.Message}");
        }

        if (manifests.Count == 0)
        {
            return File.Exists(manifestPath)
                ? new ToolResult(false, "Snapshot manifest is empty.")
                : new ToolResult(false, "No snapshot manifest is available.");
        }

        if (manifests.Count == 1)
        {
            return await BuildSingleDiffAsync(context.WorkspaceRoot, manifests[0], ct);
        }

        return await BuildWorkspaceDiffAsync(context.WorkspaceRoot, manifests, ct);
    }

    private static async Task<ToolResult> BuildSingleDiffAsync(string workspaceRoot, MutationSnapshotManifest manifest, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await AppendManifestDiffAsync(workspaceRoot, manifest, sb, ct);

        return new ToolResult(
            true,
            manifest.IsDirectory ? "Snapshot directory diff produced." : "Snapshot file diff produced.",
            sb.ToString());
    }

    private static async Task<ToolResult> BuildWorkspaceDiffAsync(
        string workspaceRoot,
        IReadOnlyList<MutationSnapshotManifest> manifests,
        CancellationToken ct)
    {
        var ordered = manifests
            .OrderBy(manifest => manifest.CapturedAtUtc)
            .ToList();
        var uniquePaths = ordered
            .Select(manifest => manifest.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"mutation_count: {ordered.Count}");
        sb.AppendLine($"unique_paths: {uniquePaths.Count}");
        sb.AppendLine($"file_mutations: {ordered.Count(manifest => !manifest.IsDirectory)}");
        sb.AppendLine($"directory_mutations: {ordered.Count(manifest => manifest.IsDirectory)}");
        sb.AppendLine($"created_paths: {ordered.Count(manifest => !manifest.ExistedBefore)}");
        sb.AppendLine("paths:");
        foreach (var path in uniquePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"  - {path}");
        }

        foreach (var manifest in ordered)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            await AppendManifestDiffAsync(workspaceRoot, manifest, sb, ct);
        }

        return new ToolResult(true, "Snapshot workspace diff produced.", sb.ToString());
    }

    private static async Task AppendManifestDiffAsync(
        string workspaceRoot,
        MutationSnapshotManifest manifest,
        StringBuilder sb,
        CancellationToken ct)
    {
        var currentPath = ToolPath.ResolveInWorkspace(workspaceRoot, manifest.RelativePath, requireExistingPath: false, allowProtectedPaths: false);
        sb.AppendLine($"path: {manifest.RelativePath}");
        sb.AppendLine($"captured_at_utc: {manifest.CapturedAtUtc:O}");
        sb.AppendLine($"existed_before: {manifest.ExistedBefore}");
        sb.AppendLine($"is_directory: {manifest.IsDirectory}");

        if (manifest.IsDirectory)
        {
            var snapshotEntries = Directory.Exists(manifest.SnapshotPath)
                ? Directory.EnumerateFileSystemEntries(manifest.SnapshotPath, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(manifest.SnapshotPath, path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            var currentEntries = Directory.Exists(currentPath)
                ? Directory.EnumerateFileSystemEntries(currentPath, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(currentPath, path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            sb.AppendLine($"change_state: {DescribeDirectoryState(manifest, currentPath, snapshotEntries, currentEntries)}");
            sb.AppendLine($"snapshot_entry_count: {snapshotEntries.Count}");
            sb.AppendLine($"current_entry_count: {currentEntries.Count}");
            sb.AppendLine("snapshot_entries:");
            foreach (var entry in snapshotEntries)
            {
                sb.AppendLine($"  - {entry}");
            }

            sb.AppendLine("current_entries:");
            foreach (var entry in currentEntries)
            {
                sb.AppendLine($"  - {entry}");
            }

            return;
        }

        var snapshotContent = File.Exists(manifest.SnapshotPath)
            ? await File.ReadAllTextAsync(manifest.SnapshotPath, ct)
            : string.Empty;
        var currentContent = File.Exists(currentPath)
            ? await File.ReadAllTextAsync(currentPath, ct)
            : string.Empty;

        sb.AppendLine($"change_state: {DescribeFileState(manifest, currentPath, snapshotContent, currentContent)}");
        sb.AppendLine($"snapshot_hash: {ComputeHash(snapshotContent)}");
        sb.AppendLine($"current_hash: {ComputeHash(currentContent)}");
        sb.AppendLine("snapshot_excerpt:");
        sb.AppendLine(Clip(snapshotContent));
        sb.AppendLine("current_excerpt:");
        sb.AppendLine(Clip(currentContent));
    }

    private static string DescribeFileState(
        MutationSnapshotManifest manifest,
        string currentPath,
        string snapshotContent,
        string currentContent)
    {
        if (!manifest.ExistedBefore && File.Exists(currentPath))
        {
            return "created";
        }

        if (manifest.ExistedBefore && !File.Exists(currentPath))
        {
            return "deleted";
        }

        return snapshotContent == currentContent ? "unchanged" : "modified";
    }

    private static string DescribeDirectoryState(
        MutationSnapshotManifest manifest,
        string currentPath,
        IReadOnlyList<string> snapshotEntries,
        IReadOnlyList<string> currentEntries)
    {
        if (!manifest.ExistedBefore && Directory.Exists(currentPath))
        {
            return "created";
        }

        if (manifest.ExistedBefore && !Directory.Exists(currentPath))
        {
            return "deleted";
        }

        return snapshotEntries.SequenceEqual(currentEntries, StringComparer.OrdinalIgnoreCase)
            ? "unchanged"
            : "modified";
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Clip(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        return value.Length <= 1200 ? value : value[..1200] + "\n[truncated]";
    }

}
