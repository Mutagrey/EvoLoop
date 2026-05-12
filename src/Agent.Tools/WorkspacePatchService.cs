using System.Security.Cryptography;
using System.Text;
using Agent.Core;

namespace Agent.Tools;

public sealed class WorkspacePatchService : IPatchService
{
    public async Task<ToolResult> WriteFileAsync(FileWriteRequest request, ToolContext context, CancellationToken ct)
    {
        var fullPath = ToolPath.ResolveInWorkspace(context.WorkspaceRoot, request.Path, requireExistingPath: false, allowProtectedPaths: false);
        if (!File.Exists(fullPath) && !request.CreateIfMissing)
        {
            return new ToolResult(false, "File does not exist and create_if_missing=false.");
        }

        if (File.Exists(fullPath) && !string.IsNullOrWhiteSpace(request.ExpectedHash))
        {
            var actual = await ComputeSha256Async(fullPath, ct);
            if (!actual.Equals(request.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ToolResult(false, $"Hash mismatch. expected_hash={request.ExpectedHash}, actual={actual}");
            }
        }

        var snapshot = await CaptureSnapshotAsync(context.WorkspaceRoot, request.Path, ct);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, request.Content, Encoding.UTF8, ct);
        await RecordMutationAsync(context, "write", request.Path, snapshot, ct);
        return new ToolResult(true, $"Wrote file: {request.Path}");
    }

    public async Task<ToolResult> ApplyPatchAsync(FilePatchRequest request, ToolContext context, CancellationToken ct)
    {
        var fullPath = ToolPath.ResolveInWorkspace(context.WorkspaceRoot, request.Path, requireExistingPath: false, allowProtectedPaths: false);
        var existing = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, ct) : string.Empty;

        if (File.Exists(fullPath) && !string.IsNullOrWhiteSpace(request.ExpectedHash))
        {
            var actual = await ComputeSha256Async(fullPath, ct);
            if (!actual.Equals(request.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ToolResult(false, $"Hash mismatch. expected_hash={request.ExpectedHash}, actual={actual}");
            }
        }

        string newContent;
        if (!string.IsNullOrWhiteSpace(request.UnifiedDiff))
        {
            var patchResult = TryApplyUnifiedDiff(existing, request.UnifiedDiff!);
            if (!patchResult.Success)
            {
                return new ToolResult(false, patchResult.ErrorMessage ?? "Unified diff apply failed.");
            }

            newContent = patchResult.Content;
        }
        else if (request.Content is not null)
        {
            newContent = request.Content;
        }
        else
        {
            return new ToolResult(false, "Provide unified_diff or content.");
        }

        var snapshot = await CaptureSnapshotAsync(context.WorkspaceRoot, request.Path, ct);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
        await RecordMutationAsync(context, "patch", request.Path, snapshot, ct);
        return new ToolResult(true, $"Patched file: {request.Path}");
    }

    public async Task<ToolResult> DeleteAsync(FileDeleteRequest request, ToolContext context, CancellationToken ct)
    {
        var fullPath = ToolPath.ResolveInWorkspace(context.WorkspaceRoot, request.Path, requireExistingPath: true, allowProtectedPaths: false);
        var snapshot = await CaptureSnapshotAsync(context.WorkspaceRoot, request.Path, ct);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            await RecordMutationAsync(context, "delete_file", request.Path, snapshot, ct);
            return new ToolResult(true, $"Deleted file: {request.Path}");
        }

        if (Directory.Exists(fullPath))
        {
            if (!request.Recursive)
            {
                return new ToolResult(false, "Directory deletion requires recursive=true.");
            }

            Directory.Delete(fullPath, recursive: true);
            await RecordMutationAsync(context, "delete_directory", request.Path, snapshot, ct);
            return new ToolResult(true, $"Deleted directory: {request.Path}");
        }

        return new ToolResult(false, $"Path not found: {request.Path}");
    }

    public async Task<ToolResult> UndoLastAsync(string workspaceRoot, CancellationToken ct)
    {
        var manifestPath = MutationSnapshotManifestStore.GetManifestPath(workspaceRoot);
        if (!File.Exists(manifestPath))
        {
            return new ToolResult(false, "No mutation snapshot is available for undo.");
        }

        MutationSnapshotManifest? manifest;
        try
        {
            manifest = await MutationSnapshotManifestStore.ReadAsync(workspaceRoot, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"Failed to read undo manifest: {ex.Message}");
        }

        if (manifest is null)
        {
            return new ToolResult(false, "Undo manifest is empty.");
        }

        var targetPath = ToolPath.ResolveInWorkspace(workspaceRoot, manifest.RelativePath, requireExistingPath: false, allowProtectedPaths: false);
        try
        {
            if (manifest.ExistedBefore)
            {
                if (manifest.IsDirectory)
                {
                    if (Directory.Exists(targetPath))
                    {
                        Directory.Delete(targetPath, true);
                    }

                    CopyDirectory(manifest.SnapshotPath, targetPath);
                }
                else
                {
                    var directory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Copy(manifest.SnapshotPath, targetPath, overwrite: true);
                }
            }
            else
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                else if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, true);
                }
            }

            File.Delete(manifestPath);
            return new ToolResult(true, $"Undo restored: {manifest.RelativePath}");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"Undo failed: {ex.Message}");
        }
    }

    private static async Task<MutationSnapshotManifest> CaptureSnapshotAsync(string workspaceRoot, string relativePath, CancellationToken ct)
    {
        var targetPath = ToolPath.ResolveInWorkspace(workspaceRoot, relativePath, requireExistingPath: false, allowProtectedPaths: false);
        var snapshotRoot = Path.Combine(workspaceRoot, ".evoloop", "storage", "snapshots");
        Directory.CreateDirectory(snapshotRoot);

        var existedBefore = File.Exists(targetPath) || Directory.Exists(targetPath);
        var isDirectory = Directory.Exists(targetPath);
        var snapshotToken = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + "-" + Guid.NewGuid().ToString("n")[..8];
        var snapshotPath = Path.Combine(snapshotRoot, snapshotToken);

        if (existedBefore)
        {
            if (isDirectory)
            {
                CopyDirectory(targetPath, snapshotPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                File.Copy(targetPath, snapshotPath, overwrite: true);
            }
        }

        var manifest = new MutationSnapshotManifest(relativePath, existedBefore, isDirectory, snapshotPath, DateTimeOffset.UtcNow);
        await MutationSnapshotManifestStore.WriteAsync(workspaceRoot, manifest, ct);
        return manifest;
    }

    private static async Task RecordMutationAsync(
        ToolContext context,
        string mutationType,
        string relativePath,
        MutationSnapshotManifest snapshot,
        CancellationToken ct)
    {
        await context.EventLog.AppendAsync(new AgentEventRecord(
            context.SessionId,
            AgentEventTypes.FileMutation,
            DateTimeOffset.UtcNow,
            mutationType,
            null,
            true,
            new Dictionary<string, string>
            {
                ["path"] = relativePath,
                ["snapshot"] = snapshot.SnapshotPath
            }), ct);
    }

    private static PatchApplyResult TryApplyUnifiedDiff(string originalContent, string diffText)
    {
        var originalLines = SplitLines(originalContent);
        var diffLines = SplitLines(diffText)
            .Where(line => !line.StartsWith("---", StringComparison.Ordinal) &&
                           !line.StartsWith("+++", StringComparison.Ordinal))
            .ToList();

        var result = new List<string>();
        var sourceIndex = 0;
        var diffIndex = 0;

        while (diffIndex < diffLines.Count)
        {
            var line = diffLines[diffIndex];
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                diffIndex++;
                continue;
            }

            var hunk = ParseHunkHeader(line);
            if (hunk is null)
            {
                return PatchApplyResult.Fail("Invalid unified diff hunk header.");
            }

            var targetIndex = Math.Max(0, hunk.Value.StartOld - 1);
            while (sourceIndex < targetIndex && sourceIndex < originalLines.Count)
            {
                result.Add(originalLines[sourceIndex++]);
            }

            diffIndex++;
            while (diffIndex < diffLines.Count && !diffLines[diffIndex].StartsWith("@@", StringComparison.Ordinal))
            {
                var patchLine = diffLines[diffIndex];
                if (patchLine.Length == 0)
                {
                    return PatchApplyResult.Fail("Invalid empty patch line.");
                }

                var prefix = patchLine[0];
                var content = patchLine.Length > 1 ? patchLine[1..] : string.Empty;
                switch (prefix)
                {
                    case ' ':
                        if (sourceIndex >= originalLines.Count || originalLines[sourceIndex] != content)
                        {
                            return PatchApplyResult.Fail("Patch context mismatch.");
                        }

                        result.Add(originalLines[sourceIndex]);
                        sourceIndex++;
                        break;
                    case '-':
                        if (sourceIndex >= originalLines.Count || originalLines[sourceIndex] != content)
                        {
                            return PatchApplyResult.Fail("Patch delete mismatch.");
                        }

                        sourceIndex++;
                        break;
                    case '+':
                        result.Add(content);
                        break;
                    case '\\':
                        break;
                    default:
                        return PatchApplyResult.Fail($"Unsupported patch line prefix '{prefix}'.");
                }

                diffIndex++;
            }
        }

        while (sourceIndex < originalLines.Count)
        {
            result.Add(originalLines[sourceIndex++]);
        }

        return PatchApplyResult.Ok(string.Join(Environment.NewLine, result));
    }

    private static (int StartOld, int CountOld, int StartNew, int CountNew)? ParseHunkHeader(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        static (int start, int count)? ParsePart(string value, char prefix)
        {
            if (value.Length == 0 || value[0] != prefix)
            {
                return null;
            }

            var numbers = value[1..].Split(',');
            if (!int.TryParse(numbers[0], out var start))
            {
                return null;
            }

            var count = numbers.Length > 1 && int.TryParse(numbers[1], out var parsedCount) ? parsedCount : 1;
            return (start, count);
        }

        var oldPart = ParsePart(parts[1], '-');
        var newPart = ParsePart(parts[2], '+');
        if (oldPart is null || newPart is null)
        {
            return null;
        }

        return (oldPart.Value.start, oldPart.Value.count, newPart.Value.start, newPart.Value.count);
    }

    private static List<string> SplitLines(string content)
    {
        return content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var destinationFile = Path.Combine(destinationPath, relative);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private sealed record PatchApplyResult(bool Success, string Content, string? ErrorMessage)
    {
        public static PatchApplyResult Ok(string content) => new(true, content, null);
        public static PatchApplyResult Fail(string error) => new(false, string.Empty, error);
    }
}
