using System.Text;
using Agent.Core;

namespace Agent.Tools;

public sealed class FsListTool : ITool
{
    public string Name => "fs_list";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.FileRead, false, Array.Empty<string>());

    public ToolSchema Schema => new(
        "List directory entries.",
        new[] { "path" },
        new Dictionary<string, string>
        {
            ["path"] = "Path relative to workspace.",
            ["recurse"] = "Whether to recurse into subdirectories.",
            ["include_hidden"] = "Whether to include hidden entries."
        });

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var path = ToolArgumentReader.GetString(call.Arguments, "path") ?? ".";
        var recurse = ToolArgumentReader.GetBool(call.Arguments, "recurse", false);
        var includeHidden = ToolArgumentReader.GetBool(call.Arguments, "include_hidden", false);

        var fullPath = ToolPath.ResolveInWorkspace(context.WorkspaceRoot, path);
        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult(new ToolResult(false, $"Directory not found: {path}"));
        }

        var sb = new StringBuilder();
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath, "*", option).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);
            if (!includeHidden && name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var isDir = Directory.Exists(entry);
            if (isDir)
            {
                sb.AppendLine($"[DIR] {Path.GetRelativePath(context.WorkspaceRoot, entry)}");
            }
            else
            {
                var info = new FileInfo(entry);
                sb.AppendLine($"[FILE] {Path.GetRelativePath(context.WorkspaceRoot, entry)} ({info.Length} bytes)");
            }
        }

        return Task.FromResult(new ToolResult(true, "Directory listed.", sb.ToString()));
    }
}

public sealed class FsReadTool : ITool
{
    public string Name => "fs_read";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.FileRead, false, Array.Empty<string>());

    public ToolSchema Schema => new(
        "Read file content.",
        new[] { "path" },
        new Dictionary<string, string>
        {
            ["path"] = "File path relative to workspace.",
            ["start_line"] = "1-based line start.",
            ["end_line"] = "1-based line end.",
            ["max_bytes"] = "Max bytes to return."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var path = ToolArgumentReader.GetString(call.Arguments, "path") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ToolResult(false, "Missing required argument: path");
        }

        var fullPath = ToolPath.ResolveInWorkspace(context.WorkspaceRoot, path);
        if (!File.Exists(fullPath))
        {
            return new ToolResult(false, $"File not found: {path}");
        }

        var startLine = Math.Max(1, ToolArgumentReader.GetInt32(call.Arguments, "start_line", 1));
        var endLine = ToolArgumentReader.GetInt32(call.Arguments, "end_line", int.MaxValue);
        var maxBytes = ToolArgumentReader.GetInt32(call.Arguments, "max_bytes", context.Config.Runtime.MaxOutputBytes);

        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        if (startLine > lines.Length)
        {
            return new ToolResult(true, "Read completed. Requested start line is beyond EOF.", string.Empty);
        }

        endLine = Math.Min(endLine, lines.Length);

        var sb = new StringBuilder();
        for (var i = startLine; i <= endLine; i++)
        {
            sb.AppendLine($"{i,5}: {lines[i - 1]}");
        }

        var output = sb.ToString();
        if (Encoding.UTF8.GetByteCount(output) > maxBytes)
        {
            while (output.Length > 0 && Encoding.UTF8.GetByteCount(output) > maxBytes)
            {
                output = output[..Math.Max(0, output.Length - 256)];
            }

            output += "\n[truncated]";
        }

        return new ToolResult(true, "Read completed.", output);
    }
}

public sealed class FsWriteTool : ITool
{
    public string Name => "fs_write";
    public ToolMetadata Metadata => new(ToolRiskLevel.Medium, ToolCategory.FileWrite, true, new[] { "workspace_write" });

    public ToolSchema Schema => new(
        "Write file content.",
        new[] { "path", "content" },
        new Dictionary<string, string>
        {
            ["path"] = "File path relative to workspace.",
            ["content"] = "Full file content to write.",
            ["create_if_missing"] = "Create file if it does not exist.",
            ["expected_hash"] = "SHA256 hash precondition of existing file content."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var path = ToolArgumentReader.GetString(call.Arguments, "path") ?? string.Empty;
        var content = ToolArgumentReader.GetString(call.Arguments, "content") ?? string.Empty;
        var createIfMissing = ToolArgumentReader.GetBool(call.Arguments, "create_if_missing", true);
        var expectedHash = ToolArgumentReader.GetString(call.Arguments, "expected_hash");

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ToolResult(false, "Missing required argument: path");
        }

        return await context.PatchService.WriteFileAsync(
            new FileWriteRequest(path, content, createIfMissing, expectedHash),
            context,
            ct);
    }
}

public sealed class FsPatchTool : ITool
{
    public string Name => "fs_patch";
    public ToolMetadata Metadata => new(ToolRiskLevel.High, ToolCategory.FileWrite, true, new[] { "workspace_write" });

    public ToolSchema Schema => new(
        "Apply a unified diff patch using git apply, or write full content fallback.",
        new[] { "path" },
        new Dictionary<string, string>
        {
            ["path"] = "Target file path relative to workspace.",
            ["unified_diff"] = "Unified diff text.",
            ["content"] = "Fallback full replacement content if unified_diff is not provided.",
            ["expected_hash"] = "SHA256 hash precondition of the current file content."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var path = ToolArgumentReader.GetString(call.Arguments, "path") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ToolResult(false, "Missing required argument: path");
        }

        var diff = ToolArgumentReader.GetString(call.Arguments, "unified_diff");
        var content = ToolArgumentReader.GetString(call.Arguments, "content");
        var expectedHash = ToolArgumentReader.GetString(call.Arguments, "expected_hash");
        return await context.PatchService.ApplyPatchAsync(
            new FilePatchRequest(path, diff, content, expectedHash),
            context,
            ct);
    }
}

public sealed class FsDeleteTool : ITool
{
    public string Name => "fs_delete";
    public ToolMetadata Metadata => new(ToolRiskLevel.Critical, ToolCategory.FileWrite, true, new[] { "workspace_write" });

    public ToolSchema Schema => new(
        "Delete file or directory.",
        new[] { "path" },
        new Dictionary<string, string>
        {
            ["path"] = "Path relative to workspace.",
            ["recursive"] = "Delete directory recursively."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var path = ToolArgumentReader.GetString(call.Arguments, "path") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ToolResult(false, "Missing required argument: path");
        }

        var recursive = ToolArgumentReader.GetBool(call.Arguments, "recursive", false);
        return await context.PatchService.DeleteAsync(new FileDeleteRequest(path, recursive), context, ct);
    }
}
