using Agent.Core;

namespace Agent.Hosting;

public sealed class AgentRuntimeContext
{
    internal AgentRuntimeContext(
        string requestedWorkspace,
        string workspace,
        string configPath,
        AgentConfig config,
        RuntimeCapabilities capabilities)
    {
        RequestedWorkspace = requestedWorkspace;
        Workspace = workspace;
        ConfigPath = configPath;
        Config = config;
        Capabilities = capabilities;
    }

    internal AgentRuntimeContext(
        string requestedWorkspace,
        string workspace,
        AgentConfig config,
        RuntimeCapabilities capabilities)
        : this(requestedWorkspace, workspace, AgentConfigLoader.GetDefaultConfigPath(), config, capabilities)
    {
    }

    public string RequestedWorkspace { get; }
    public string Workspace { get; }
    public string ConfigPath { get; }
    public AgentConfig Config { get; }
    public RuntimeCapabilities Capabilities { get; }

    public static async Task<AgentRuntimeContext> CreateAsync(AgentRuntimeOptions options, CancellationToken ct)
    {
        var requestedWorkspace = Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory());
        var workspace = await AgentStartup.ResolveWorkspaceRootAsync(requestedWorkspace, ct);
        var configPath = Path.GetFullPath(options.ConfigPath ?? AgentConfigLoader.GetDefaultConfigPath());
        var config = AgentStartup.BuildEffectiveConfig(AgentConfigLoader.LoadOrCreate(configPath), options.OfflineStrict);
        var capabilities = await RuntimeCapabilityProbe.ProbeAsync(config, workspace, ct);

        return new AgentRuntimeContext(requestedWorkspace, workspace, configPath, config, capabilities);
    }
}
