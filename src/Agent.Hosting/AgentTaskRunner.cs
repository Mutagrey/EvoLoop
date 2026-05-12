using System.Text;
using Agent.Core;
using Agent.Providers;
using Agent.Tools;

namespace Agent.Hosting;

public sealed class AgentTaskRunner
{
    private readonly AgentExecutionHost _host;
    private readonly AgentRuntimeContext _context;

    public AgentTaskRunner(AgentExecutionHost host, AgentRuntimeContext context)
    {
        _host = host;
        _context = context;
    }

    public async Task<AgentTaskRunResult> RunAsync(
        string task,
        string profile,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IAgentRunObserver? observer,
        CancellationToken ct)
    {
        if (!_context.Capabilities.CanRunAgentTasks)
        {
            if (executionMode == AgentExecutionMode.Review)
            {
                return await RunLocalReviewAsync(ct);
            }

            return new AgentTaskRunResult(
                new AgentRunResult(
                    false,
                    $"Task was not started because model execution is unavailable. {_context.Capabilities.ModelStatus}.",
                    0,
                    "not-started",
                    Array.Empty<SessionStep>()),
                null);
        }

        var result = await _host.Loop.RunAsync(new AgentRunRequest(
            task,
            _context.Workspace,
            profile,
            executionMode,
            approvalMode,
            null,
            observer),
            ct);

        return new AgentTaskRunResult(result, null);
    }

    public static string BuildReviewTask(string? suffix)
    {
        const string baseTask = "Review current workspace changes. Prefer git_diff if git is available; otherwise use workspace_snapshot_diff. Prioritize bugs, regressions, risky behavior changes, and missing tests.";
        return string.IsNullOrWhiteSpace(suffix) ? baseTask : baseTask + "\nFocus: " + suffix.Trim();
    }

    private async Task<AgentTaskRunResult> RunLocalReviewAsync(CancellationToken ct)
    {
        var summary = new StringBuilder();
        if (_context.Capabilities.GitAvailable)
        {
            var status = await ProcessRunner.RunAsync("git", new[] { "status", "--short", "--branch" }, _context.Workspace, ct, 32 * 1024);
            var diff = await ProcessRunner.RunAsync("git", new[] { "diff", "--stat" }, _context.Workspace, ct, 32 * 1024);
            var fullDiff = await ProcessRunner.RunAsync("git", new[] { "diff", "--no-ext-diff" }, _context.Workspace, ct, 128 * 1024);
            summary.AppendLine("git status:");
            summary.AppendLine(string.IsNullOrWhiteSpace(status.StdOut) ? "<empty>" : status.StdOut.Trim());
            summary.AppendLine();
            summary.AppendLine("git diff --stat:");
            summary.AppendLine(string.IsNullOrWhiteSpace(diff.StdOut) ? "<empty>" : diff.StdOut.Trim());
            summary.AppendLine();
            summary.AppendLine("git diff:");
            summary.AppendLine(string.IsNullOrWhiteSpace(fullDiff.StdOut) ? "<empty>" : fullDiff.StdOut.Trim());
        }
        else
        {
            var snapshotResult = await new WorkspaceSnapshotDiffTool().ExecuteAsync(
                new ToolCall("workspace_snapshot_diff", default, "local review"),
                new ToolContext(
                    _context.Workspace,
                    "local-review",
                    "review",
                    AgentExecutionMode.Review,
                    ApprovalPolicyMode.ReadOnly,
                    new AgentConfig(),
                    new HybridSearchService(new DisabledModelClientRouter("disabled"), new AgentConfig(), _context.Workspace),
                    _context.Capabilities,
                    _host.PatchService,
                    NullEventLog.Instance),
                ct);

            summary.AppendLine(snapshotResult.Message);
            if (!string.IsNullOrWhiteSpace(snapshotResult.StdOut))
            {
                summary.AppendLine(snapshotResult.StdOut);
            }
        }

        return new AgentTaskRunResult(
            new AgentRunResult(true, "Local review summary generated without model execution.", 0, "local-review", Array.Empty<SessionStep>()),
            summary.ToString().TrimEnd());
    }
}

public sealed record AgentTaskRunResult(AgentRunResult Result, string? LocalReviewSummary);
