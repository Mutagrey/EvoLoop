using Agent.Core;
using Agent.Hosting;

namespace Agent.Tui;

internal interface ITuiTaskRunner
{
    Task<AgentTaskRunResult> RunAsync(
        string task,
        string profile,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IAgentRunObserver? observer,
        CancellationToken ct);
}

internal sealed class TuiTaskRunner : ITuiTaskRunner
{
    private readonly AgentTaskRunner _inner;

    public TuiTaskRunner(AgentTaskRunner inner)
    {
        _inner = inner;
    }

    public Task<AgentTaskRunResult> RunAsync(
        string task,
        string profile,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IAgentRunObserver? observer,
        CancellationToken ct)
    {
        return _inner.RunAsync(task, profile, executionMode, approvalMode, observer, ct);
    }
}
