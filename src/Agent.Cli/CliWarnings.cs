using Agent.Hosting;

namespace Agent.Cli;

internal static class CliWarnings
{
    public static void Write(AnsiRenderer renderer, AgentRuntimeContext context)
    {
        if (!context.Workspace.Equals(context.RequestedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            renderer.WriteInfo($"Workspace resolved to git root: {context.Workspace}");
        }

        if (context.Config.Safety.OfflineStrictMode)
        {
            renderer.WriteWarn("Offline strict mode is ON. Network shell commands are blocked except approved gateway hosts.");
        }

        if (!AgentStartup.HasApiAuthConfigured(context.Config))
        {
            renderer.WriteWarn(
                $"API auth is not configured. Set env var '{context.Config.Api.ApiKeyEnvVar}', or set api.apiKey, or configure auth headers in config.");
        }

        if (!context.Capabilities.WorkspaceWritable)
        {
            renderer.WriteWarn("Workspace storage is unavailable. Session persistence and memory are disabled for this run.");
        }

        if (!context.Capabilities.GitAvailable)
        {
            renderer.WriteWarn("git is not available. Git tools will report a clear unavailable status.");
        }

        if (!context.Capabilities.RipgrepAvailable)
        {
            renderer.WriteWarn("rg is not available. Search will use the built-in scanner fallback.");
        }

        if (!context.Capabilities.CanRunAgentTasks)
        {
            renderer.WriteWarn($"Agent is running in '{context.Capabilities.ModeLabel}' mode: {context.Capabilities.ModelStatus}.");
        }
    }
}
