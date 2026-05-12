using Agent.Hosting;

namespace Agent.Tui;

internal sealed record TuiRuntimeInfo(
    string Workspace,
    string RequestedWorkspace,
    string Profile,
    string ModeLabel,
    string ModelStatus,
    string ApprovalMode,
    bool OfflineStrict,
    bool ApiAuthConfigured,
    bool CanRunAgentTasks)
{
    public static TuiRuntimeInfo From(AgentRuntimeContext context, TuiArguments arguments)
    {
        return new TuiRuntimeInfo(
            context.Workspace,
            context.RequestedWorkspace,
            arguments.Profile,
            context.Capabilities.ModeLabel,
            context.Capabilities.ModelStatus,
            context.Config.Safety.DefaultApprovalMode.ToString(),
            context.Config.Safety.OfflineStrictMode,
            AgentStartup.HasApiAuthConfigured(context.Config),
            context.Capabilities.CanRunAgentTasks);
    }
}
