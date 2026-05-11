using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;

namespace Agent.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ApplyPrivacyDefaults();

        try
        {
            var command = CliArguments.Parse(args);
            var requestedWorkspace = Path.GetFullPath(command.Workspace ?? Directory.GetCurrentDirectory());
            var workspace = await ResolveWorkspaceRootAsync(requestedWorkspace, CancellationToken.None);
            var config = BuildEffectiveConfig(AgentConfigLoader.LoadOrCreate(command.ConfigPath), command);
            var useColor = command.NoColor ? false : config.Ui.UseColor;
            var renderer = new AnsiRenderer(useColor, config.Ui.CompactMode);
            var capabilities = await RuntimeCapabilityProbe.ProbeAsync(config, workspace, CancellationToken.None);

            if (!workspace.Equals(requestedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                renderer.WriteInfo($"Workspace resolved to git root: {workspace}");
            }

            if (config.Safety.OfflineStrictMode)
            {
                renderer.WriteWarn("Offline strict mode is ON. Network shell commands are blocked except approved gateway hosts.");
            }

            if (!HasApiAuthConfigured(config))
            {
                renderer.WriteWarn(
                    $"API auth is not configured. Set env var '{config.Api.ApiKeyEnvVar}', or set api.apiKey, or configure auth headers in config.");
            }

            WriteCapabilityWarnings(renderer, capabilities);

            if (command.Mode == CliMode.Doctor)
            {
                renderer.WriteHeader("EvoLoop Doctor");
                renderer.WritePanel("Capabilities", capabilities.ToDisplayText());
                return 0;
            }

            ModelClientRouter? liveRouter = null;
            IModelClientRouter modelRouter;
            if (capabilities.CanRunAgentTasks)
            {
                liveRouter = new ModelClientRouter(config);
                modelRouter = liveRouter;
            }
            else
            {
                modelRouter = new DisabledModelClientRouter(
                    $"Model execution is unavailable because the agent is running in '{capabilities.ModeLabel}' mode. Run 'agent doctor' to inspect gateway and environment status.");
            }

            var tools = ToolCatalog.CreateDefaultTools();
            var patchService = new WorkspacePatchService();
            IEventLog eventLog = capabilities.WorkspaceWritable
                ? new JsonlEventLog(workspace)
                : NullEventLog.Instance;
            var searchService = new HybridSearchService(modelRouter, config, workspace);
            var contextFactory = new DefaultToolContextFactory(config, searchService, patchService, eventLog, capabilities);
            var policy = new DefaultPolicyEngine(tools, config);
            var approval = new ConsoleApprovalService(renderer);
            IEventStore eventStore = capabilities.WorkspaceWritable
                ? new HybridEventStore(workspace)
                : NullEventStore.Instance;
            IWorkspaceMemoryStore memoryStore = config.Runtime.MemoryEnabled && capabilities.WorkspaceWritable
                ? new WorkspaceMemoryStore(workspace, config)
                : NullWorkspaceMemoryStore.Instance;
            var loop = new ReActAgentLoop(modelRouter, tools, policy, approval, eventStore, contextFactory, config, memoryStore);
            try
            {
                if (command.Mode is CliMode.Run or CliMode.Plan or CliMode.Review)
                {
                    var task = command.Mode == CliMode.Review
                        ? CliSession.BuildReviewTask(command.Task)
                        : command.Task;
                    if (string.IsNullOrWhiteSpace(task))
                    {
                        renderer.WriteError("Missing task. Usage: agent run|plan \"your task\" [--profile reasoning|fast|fallback]");
                        return 2;
                    }

                    var result = await CliSession.RunTaskAsync(
                        loop,
                        renderer,
                        task,
                        workspace,
                        command.Profile,
                        capabilities,
                        command.Mode switch
                        {
                            CliMode.Plan => AgentExecutionMode.Plan,
                            CliMode.Review => AgentExecutionMode.Review,
                            _ => AgentExecutionMode.Run
                        },
                        config.Safety.DefaultApprovalMode,
                        patchService);
                    return result.Success ? 0 : 1;
                }

                await CliSession.RunReplAsync(loop, tools, renderer, config, workspace, command.Profile, memoryStore, capabilities, patchService);
                return 0;
            }
            finally
            {
                liveRouter?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ApplyPrivacyDefaults()
    {
        SetIfMissing("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        SetIfMissing("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
        SetIfMissing("DOTNET_NOLOGO", "1");
    }

    private static void SetIfMissing(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static async Task<string> ResolveWorkspaceRootAsync(string requestedWorkspace, CancellationToken ct)
    {
        var root = Path.GetFullPath(requestedWorkspace);
        if (!Directory.Exists(root))
        {
            return root;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                new[] { "rev-parse", "--show-toplevel" },
                root,
                ct,
                8 * 1024);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return root;
            }

            var line = result.StdOut
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return root;
            }

            var gitRoot = Path.GetFullPath(line.Trim());
            return Directory.Exists(gitRoot) ? gitRoot : root;
        }
        catch
        {
            return root;
        }
    }

    private static AgentConfig BuildEffectiveConfig(AgentConfig loadedConfig, CliArguments command)
    {
        if (!command.OfflineStrict)
        {
            return loadedConfig;
        }

        var safety = loadedConfig.Safety;
        var allowedHosts = new List<string>(safety.AllowedNetworkHosts);
        if (Uri.TryCreate(loadedConfig.Api.BaseUrl, UriKind.Absolute, out var baseUri) &&
            !string.IsNullOrWhiteSpace(baseUri.Host) &&
            !allowedHosts.Contains(baseUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            allowedHosts.Add(baseUri.Host);
        }

        return new AgentConfig
        {
            Api = loadedConfig.Api,
            Models = loadedConfig.Models,
            Workspace = loadedConfig.Workspace,
            Runtime = loadedConfig.Runtime,
            Ui = loadedConfig.Ui,
            Safety = new SafetyConfig
            {
                RequireApprovalForWrites = safety.RequireApprovalForWrites,
                RequireApprovalForCommits = safety.RequireApprovalForCommits,
                RequireApprovalForRiskyShell = safety.RequireApprovalForRiskyShell,
                DenyOutsideWorkspace = safety.DenyOutsideWorkspace,
                OfflineStrictMode = true,
                DefaultApprovalMode = safety.DefaultApprovalMode,
                AllowedNetworkHosts = allowedHosts,
                DeniedShellPatterns = safety.DeniedShellPatterns
            }
        };
    }

    private static void WriteCapabilityWarnings(AnsiRenderer renderer, RuntimeCapabilities capabilities)
    {
        if (!capabilities.WorkspaceWritable)
        {
            renderer.WriteWarn("Workspace storage is unavailable. Session persistence and memory are disabled for this run.");
        }

        if (!capabilities.GitAvailable)
        {
            renderer.WriteWarn("git is not available. Git tools will report a clear unavailable status.");
        }

        if (!capabilities.RipgrepAvailable)
        {
            renderer.WriteWarn("rg is not available. Search will use the built-in scanner fallback.");
        }

        if (!capabilities.CanRunAgentTasks)
        {
            renderer.WriteWarn($"Agent is running in '{capabilities.ModeLabel}' mode: {capabilities.ModelStatus}.");
        }
    }
    
    private static bool HasApiAuthConfigured(AgentConfig config)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.Api.ApiKeyEnvVar)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(config.Api.ApiKey))
        {
            return true;
        }

        if (config.Api.Headers.ContainsKey("Authorization"))
        {
            return true;
        }

        if (config.Api.Headers.Keys.Any(k =>
                k.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("Api-Key", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
