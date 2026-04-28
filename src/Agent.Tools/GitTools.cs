using Agent.Core;

namespace Agent.Tools;

public sealed class GitStatusTool : ITool
{
    public string Name => "git_status";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Git, false, new[] { "git" });

    public ToolSchema Schema => new(
        "Show git status.",
        Array.Empty<string>(),
        new Dictionary<string, string>());

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.GitAvailable)
        {
            return new ToolResult(false, "git is unavailable in the current environment.");
        }

        var result = await ProcessRunner.RunAsync(
            "git",
            new[] { "status", "--short", "--branch" },
            context.WorkspaceRoot,
            ct,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "git status completed" : "git status failed", result.StdOut, result.StdErr);
    }
}

public sealed class GitDiffTool : ITool
{
    public string Name => "git_diff";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Git, false, new[] { "git" });

    public ToolSchema Schema => new(
        "Show git diff.",
        Array.Empty<string>(),
        new Dictionary<string, string>
        {
            ["staged"] = "Whether to show staged diff.",
            ["path"] = "Optional path filter."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.GitAvailable)
        {
            return new ToolResult(false, "git is unavailable in the current environment.");
        }

        var staged = ToolArgumentReader.GetBool(call.Arguments, "staged", false);
        var path = ToolArgumentReader.GetString(call.Arguments, "path");

        var args = new List<string> { "diff" };
        if (staged)
        {
            args.Add("--staged");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        var result = await ProcessRunner.RunAsync(
            "git",
            args,
            context.WorkspaceRoot,
            ct,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "git diff completed" : "git diff failed", result.StdOut, result.StdErr);
    }
}

public sealed class GitLogTool : ITool
{
    public string Name => "git_log";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Git, false, new[] { "git" });

    public ToolSchema Schema => new(
        "Show recent git commits.",
        Array.Empty<string>(),
        new Dictionary<string, string>
        {
            ["max_count"] = "Maximum commits to return."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.GitAvailable)
        {
            return new ToolResult(false, "git is unavailable in the current environment.");
        }

        var maxCount = Math.Clamp(ToolArgumentReader.GetInt32(call.Arguments, "max_count", 20), 1, 200);

        var result = await ProcessRunner.RunAsync(
            "git",
            new[] { "log", "--oneline", $"--max-count={maxCount}" },
            context.WorkspaceRoot,
            ct,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "git log completed" : "git log failed", result.StdOut, result.StdErr);
    }
}

public sealed class GitShowTool : ITool
{
    public string Name => "git_show";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Git, false, new[] { "git" });

    public ToolSchema Schema => new(
        "Show a commit or object.",
        new[] { "ref" },
        new Dictionary<string, string>
        {
            ["ref"] = "Commit SHA, branch, or tag to inspect."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.GitAvailable)
        {
            return new ToolResult(false, "git is unavailable in the current environment.");
        }

        var gitRef = ToolArgumentReader.GetString(call.Arguments, "ref") ?? "HEAD";

        var result = await ProcessRunner.RunAsync(
            "git",
            new[] { "show", "--stat", gitRef },
            context.WorkspaceRoot,
            ct,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "git show completed" : "git show failed", result.StdOut, result.StdErr);
    }
}

public sealed class GitAddTool : ITool
{
    public string Name => "git_add";
    public ToolMetadata Metadata => new(ToolRiskLevel.High, ToolCategory.Git, true, new[] { "git" });

    public ToolSchema Schema => new(
        "Stage files.",
        Array.Empty<string>(),
        new Dictionary<string, string>
        {
            ["pathspec"] = "Pathspec to stage. Defaults to all."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.GitAvailable)
        {
            return new ToolResult(false, "git is unavailable in the current environment.");
        }

        var pathspec = ToolArgumentReader.GetString(call.Arguments, "pathspec") ?? ".";

        var result = await ProcessRunner.RunAsync(
            "git",
            new[] { "add", "--", pathspec },
            context.WorkspaceRoot,
            ct,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "git add completed" : "git add failed", result.StdOut, result.StdErr);
    }
}

public sealed class GitCommitTool : ITool
{
    public string Name => "git_commit";
    public ToolMetadata Metadata => new(ToolRiskLevel.Critical, ToolCategory.Git, true, new[] { "git" });

    public ToolSchema Schema => new(
        "Create git commit.",
        new[] { "message" },
        new Dictionary<string, string>
        {
            ["message"] = "Commit message."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.GitAvailable)
        {
            return new ToolResult(false, "git is unavailable in the current environment.");
        }

        var message = ToolArgumentReader.GetString(call.Arguments, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ToolResult(false, "Missing commit message.");
        }

        var result = await ProcessRunner.RunAsync(
            "git",
            new[] { "commit", "-m", message },
            context.WorkspaceRoot,
            ct,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "git commit completed" : "git commit failed", result.StdOut, result.StdErr);
    }
}
