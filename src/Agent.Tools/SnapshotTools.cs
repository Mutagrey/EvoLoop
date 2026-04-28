using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var manifestPath = Path.Combine(context.WorkspaceRoot, ".evoloop", "storage", "snapshots", "last-mutation.json");
        if (!File.Exists(manifestPath))
        {
            return new ToolResult(false, "No snapshot manifest is available.");
        }

        MutationSnapshotManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MutationSnapshotManifest>(await File.ReadAllTextAsync(manifestPath, ct));
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"Failed to read snapshot manifest: {ex.Message}");
        }

        if (manifest is null)
        {
            return new ToolResult(false, "Snapshot manifest is empty.");
        }

        var currentPath = ToolPath.ResolveInWorkspace(context.WorkspaceRoot, manifest.RelativePath, requireExistingPath: false, allowProtectedPaths: false);
        var sb = new StringBuilder();
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

            return new ToolResult(true, "Snapshot directory diff produced.", sb.ToString());
        }

        var snapshotContent = File.Exists(manifest.SnapshotPath)
            ? await File.ReadAllTextAsync(manifest.SnapshotPath, ct)
            : string.Empty;
        var currentContent = File.Exists(currentPath)
            ? await File.ReadAllTextAsync(currentPath, ct)
            : string.Empty;

        sb.AppendLine($"snapshot_hash: {ComputeHash(snapshotContent)}");
        sb.AppendLine($"current_hash: {ComputeHash(currentContent)}");
        sb.AppendLine("snapshot_excerpt:");
        sb.AppendLine(Clip(snapshotContent));
        sb.AppendLine("current_excerpt:");
        sb.AppendLine(Clip(currentContent));

        return new ToolResult(true, "Snapshot file diff produced.", sb.ToString());
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

    private sealed record MutationSnapshotManifest(
        string RelativePath,
        bool ExistedBefore,
        bool IsDirectory,
        string SnapshotPath,
        DateTimeOffset CapturedAtUtc);
}
