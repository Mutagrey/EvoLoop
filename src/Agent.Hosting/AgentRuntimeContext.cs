using Agent.Core;

namespace Agent.Hosting;

public sealed class AgentRuntimeContext
{
    internal AgentRuntimeContext(
        string requestedWorkspace,
        string workspace,
        AgentConfig config,
        RuntimeCapabilities capabilities)
    {
        RequestedWorkspace = requestedWorkspace;
        Workspace = workspace;
        Config = config;
        Capabilities = capabilities;
    }

    public string RequestedWorkspace { get; }
    public string Workspace { get; }
    public AgentConfig Config { get; }
    public RuntimeCapabilities Capabilities { get; }

    public static async Task<AgentRuntimeContext> CreateAsync(AgentRuntimeOptions options, CancellationToken ct)
    {
        var requestedWorkspace = Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory());
        var workspace = await AgentStartup.ResolveWorkspaceRootAsync(requestedWorkspace, ct);
        var config = AgentStartup.BuildEffectiveConfig(AgentConfigLoader.LoadOrCreate(options.ConfigPath), options.OfflineStrict);
        var capabilities = await RuntimeCapabilityProbe.ProbeAsync(config, workspace, ct);

        return new AgentRuntimeContext(requestedWorkspace, workspace, config, capabilities);
    }
}
