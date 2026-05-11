using Agent.Core;

namespace Agent.Cli;

internal sealed class CliRuntimeContext
{
    private CliRuntimeContext(
        CliArguments command,
        string requestedWorkspace,
        string workspace,
        AgentConfig config,
        AnsiRenderer renderer,
        RuntimeCapabilities capabilities)
    {
        Command = command;
        RequestedWorkspace = requestedWorkspace;
        Workspace = workspace;
        Config = config;
        Renderer = renderer;
        Capabilities = capabilities;
    }

    public CliArguments Command { get; }
    public string RequestedWorkspace { get; }
    public string Workspace { get; }
    public AgentConfig Config { get; }
    public AnsiRenderer Renderer { get; }
    public RuntimeCapabilities Capabilities { get; }

    public static async Task<CliRuntimeContext> CreateAsync(CliArguments command, CancellationToken ct)
    {
        var requestedWorkspace = Path.GetFullPath(command.Workspace ?? Directory.GetCurrentDirectory());
        var workspace = await CliStartup.ResolveWorkspaceRootAsync(requestedWorkspace, ct);
        var config = CliStartup.BuildEffectiveConfig(AgentConfigLoader.LoadOrCreate(command.ConfigPath), command);
        var useColor = command.NoColor ? false : config.Ui.UseColor;
        var renderer = new AnsiRenderer(useColor, config.Ui.CompactMode);
        var capabilities = await RuntimeCapabilityProbe.ProbeAsync(config, workspace, ct);

        return new CliRuntimeContext(command, requestedWorkspace, workspace, config, renderer, capabilities);
    }

    public void WriteStartupWarnings()
    {
        if (!Workspace.Equals(RequestedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            Renderer.WriteInfo($"Workspace resolved to git root: {Workspace}");
        }

        if (Config.Safety.OfflineStrictMode)
        {
            Renderer.WriteWarn("Offline strict mode is ON. Network shell commands are blocked except approved gateway hosts.");
        }

        if (!CliStartup.HasApiAuthConfigured(Config))
        {
            Renderer.WriteWarn(
                $"API auth is not configured. Set env var '{Config.Api.ApiKeyEnvVar}', or set api.apiKey, or configure auth headers in config.");
        }

        if (!Capabilities.WorkspaceWritable)
        {
            Renderer.WriteWarn("Workspace storage is unavailable. Session persistence and memory are disabled for this run.");
        }

        if (!Capabilities.GitAvailable)
        {
            Renderer.WriteWarn("git is not available. Git tools will report a clear unavailable status.");
        }

        if (!Capabilities.RipgrepAvailable)
        {
            Renderer.WriteWarn("rg is not available. Search will use the built-in scanner fallback.");
        }

        if (!Capabilities.CanRunAgentTasks)
        {
            Renderer.WriteWarn($"Agent is running in '{Capabilities.ModeLabel}' mode: {Capabilities.ModelStatus}.");
        }
    }
}
