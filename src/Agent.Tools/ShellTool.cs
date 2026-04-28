using Agent.Core;

namespace Agent.Tools;

public sealed class ExecShellTool : ITool
{
    public string Name => "exec_shell";

    public ToolSchema Schema => new(
        "Execute a shell command in workspace.",
        new[] { "command" },
        new Dictionary<string, string>
        {
            ["command"] = "Shell command to run.",
            ["cwd"] = "Optional working directory relative to workspace.",
            ["timeout_sec"] = "Optional timeout seconds."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!context.Capabilities.ShellAvailable)
        {
            return new ToolResult(false, "Shell execution is unavailable in the current environment.");
        }

        var command = ToolArgumentReader.GetString(call.Arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolResult(false, "Missing required argument: command");
        }

        var cwdArg = ToolArgumentReader.GetString(call.Arguments, "cwd");
        string workingDirectory;
        try
        {
            workingDirectory = string.IsNullOrWhiteSpace(cwdArg)
                ? context.WorkspaceRoot
                : ToolPath.ResolveInWorkspace(context.WorkspaceRoot, cwdArg);
        }
        catch (InvalidOperationException ex)
        {
            return new ToolResult(false, ex.Message);
        }

        if (!Directory.Exists(workingDirectory))
        {
            return new ToolResult(false, $"Working directory not found: {cwdArg}");
        }

        var timeoutSec = Math.Clamp(ToolArgumentReader.GetInt32(call.Arguments, "timeout_sec", context.Config.Runtime.ToolTimeoutSeconds), 1, 900);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var result = await ProcessRunner.RunShellAsync(
            command,
            workingDirectory,
            timeoutCts.Token,
            context.Config.Runtime.MaxOutputBytes);

        return new ToolResult(result.Success, result.Success ? "Shell command completed" : "Shell command failed", result.StdOut, result.StdErr);
    }
}
