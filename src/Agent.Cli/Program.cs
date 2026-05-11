using System.Text;
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
                        ? BuildReviewTask(command.Task)
                        : command.Task;
                    if (string.IsNullOrWhiteSpace(task))
                    {
                        renderer.WriteError("Missing task. Usage: agent run|plan \"your task\" [--profile reasoning|fast|fallback]");
                        return 2;
                    }

                    var result = await RunTaskAsync(
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

                await RunReplAsync(loop, tools, renderer, config, workspace, command.Profile, memoryStore, capabilities, patchService);
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

    private static async Task RunReplAsync(
        IAgentLoop loop,
        IReadOnlyList<ITool> tools,
        AnsiRenderer renderer,
        AgentConfig config,
        string workspace,
        string profile,
        IWorkspaceMemoryStore memoryStore,
        RuntimeCapabilities capabilities,
        IPatchService patchService)
    {
        renderer.WriteHeader("EvoLoop Agent CLI");
        renderer.WritePanel(
            "Session",
            $"Workspace: {workspace}\nProfile: {profile}\nMode: {capabilities.ModeLabel}\nCommands: /task, /plan, /review, /status, /tools, /history, /memory, /cmdlog, /config, /doctor, /undo, /exit\nRecall: !N");

        AgentRunResult? lastRun = null;
        AgentRunResult? lastPlan = null;
        var commandHistory = await ReplCommandHistory.OpenAsync(workspace, 300, CancellationToken.None);

        while (true)
        {
            Console.Write("\nagent> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                break;
            }

            input = input.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.StartsWith("!", StringComparison.Ordinal))
            {
                if (!commandHistory.TryResolve(input, out var recalled))
                {
                    renderer.WriteWarn("Unknown history index. Use /cmdlog to list saved commands.");
                    continue;
                }

                renderer.WriteInfo($"Recalled: {recalled}");
                input = recalled;
            }

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.StartsWith("/task ", StringComparison.OrdinalIgnoreCase))
            {
                var task = input[6..].Trim();
                if (string.IsNullOrWhiteSpace(task))
                {
                    renderer.WriteWarn("Task text is empty.");
                    continue;
                }

                await commandHistory.AddAsync(task, CancellationToken.None);
                lastRun = await RunTaskAsync(loop, renderer, task, workspace, profile, capabilities, AgentExecutionMode.Run, config.Safety.DefaultApprovalMode, patchService);
                continue;
            }

            if (input.StartsWith("/plan ", StringComparison.OrdinalIgnoreCase))
            {
                var task = input[6..].Trim();
                if (string.IsNullOrWhiteSpace(task))
                {
                    renderer.WriteWarn("Plan task text is empty.");
                    continue;
                }

                await commandHistory.AddAsync(input, CancellationToken.None);
                lastPlan = await RunTaskAsync(loop, renderer, task, workspace, profile, capabilities, AgentExecutionMode.Plan, ApprovalPolicyMode.ReadOnly, patchService);
                continue;
            }

            if (input.Equals("/plan", StringComparison.OrdinalIgnoreCase))
            {
                if (lastPlan is null)
                {
                    renderer.WriteInfo("No plan has been generated yet.");
                }
                else
                {
                    renderer.WritePanel("Last Plan", lastPlan.FinalMessage);
                }
                continue;
            }

            if (input.StartsWith("/review", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = input.Length > 7 ? input[7..].Trim() : null;
                lastRun = await RunTaskAsync(loop, renderer, BuildReviewTask(suffix), workspace, profile, capabilities, AgentExecutionMode.Review, ApprovalPolicyMode.ReadOnly, patchService);
                continue;
            }

            if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                if (lastRun is null)
                {
                    renderer.WriteInfo("No task executed yet.");
                }
                else
                {
                    renderer.WritePanel(
                        "Status",
                        $"Session: {lastRun.SessionId}\nSuccess: {lastRun.Success}\nSteps: {lastRun.Steps}");
                }
                continue;
            }

            if (input.Equals("/tools", StringComparison.OrdinalIgnoreCase))
            {
                var body = string.Join(Environment.NewLine, tools.OrderBy(t => t.Name).Select(t => $"- {t.Name}: {t.Schema.Description} | risk={t.Metadata.RiskLevel} | category={t.Metadata.Category}"));
                renderer.WritePanel("Tools", body);
                continue;
            }

            if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
            {
                if (lastRun is null)
                {
                    renderer.WriteInfo("No run history available.");
                }
                else
                {
                    var body = new StringBuilder();
                    foreach (var step in lastRun.StepTrace)
                    {
                        body.AppendLine($"#{step.StepNumber} {step.ToolName} success={step.Success} duration={step.DurationMs}ms");
                        if (!string.IsNullOrWhiteSpace(step.Error))
                        {
                            body.AppendLine($"  error: {step.Error}");
                        }
                    }

                    renderer.WritePanel("Last Run History", body.ToString());
                }
                continue;
            }

            if (input.Equals("/config", StringComparison.OrdinalIgnoreCase))
            {
                var configPath = AgentConfigLoader.GetDefaultConfigPath();
                var models = string.Join(", ", config.Models.Keys.OrderBy(x => x));
                var hosts = config.Safety.AllowedNetworkHosts.Count == 0 ? "<none>" : string.Join(", ", config.Safety.AllowedNetworkHosts);
                var apiKeyInEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.Api.ApiKeyEnvVar));
                var apiKeyInConfig = !string.IsNullOrWhiteSpace(config.Api.ApiKey);
                var apiKeyState = (apiKeyInEnv || apiKeyInConfig) ? "present" : "missing";
                var apiKeySource = apiKeyInEnv ? "env" : (apiKeyInConfig ? "config" : "none");
                renderer.WritePanel(
                    "Config",
                    $"Path: {configPath}\nProfiles: {models}\nAPI URL: {config.Api.BaseUrl}\nOpenAI Path: {config.Api.OpenAiCompatiblePath}\nCustom Path: {config.Api.CustomPath}\nSystemPromptMode: {config.Api.SystemPromptMode}\nSystemPromptFallbackToUserMessage: {config.Api.SystemPromptFallbackToUserMessage}\nApiKeyEnvVar: {config.Api.ApiKeyEnvVar}\nApiKey: {apiKeyState} ({apiKeySource})\nOfflineStrict: {config.Safety.OfflineStrictMode}\nAllowedHosts: {hosts}\nMemoryEnabled: {config.Runtime.MemoryEnabled}\nAdaptivePrompting: {config.Runtime.AdaptivePromptingEnabled}");
                continue;
            }

            if (input.Equals("/doctor", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WritePanel("Capabilities", capabilities.ToDisplayText());
                continue;
            }

            if (input.Equals("/memory", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.Runtime.MemoryEnabled)
                {
                    renderer.WriteInfo("Memory is disabled in runtime config.");
                    continue;
                }

                var memory = await memoryStore.LoadContextAsync(workspace, "workspace overview", CancellationToken.None);
                if (string.IsNullOrWhiteSpace(memory.Content))
                {
                    renderer.WriteInfo("No workspace memory available yet.");
                }
                else
                {
                    renderer.WritePanel("Workspace Memory", memory.Content);
                }

                continue;
            }

            if (input.Equals("/undo", StringComparison.OrdinalIgnoreCase))
            {
                var undoResult = await patchService.UndoLastAsync(workspace, CancellationToken.None);
                renderer.WritePanel(undoResult.Success ? "Undo" : "Undo Failed", undoResult.Message);
                continue;
            }

            if (input.Equals("/cmdlog", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WritePanel("Saved Commands", commandHistory.FormatRecent(30));
                continue;
            }

            if (input.Equals("/approve", StringComparison.OrdinalIgnoreCase) || input.Equals("/deny", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WriteInfo("Approvals are handled inline when a risky action is requested.");
                continue;
            }

            await commandHistory.AddAsync(input, CancellationToken.None);
            lastRun = await RunTaskAsync(loop, renderer, input, workspace, profile, capabilities, AgentExecutionMode.Run, config.Safety.DefaultApprovalMode, patchService);
        }

        renderer.WriteInfo("Goodbye.");
    }

    private static async Task<AgentRunResult> RunTaskAsync(
        IAgentLoop loop,
        AnsiRenderer renderer,
        string task,
        string workspace,
        string profile,
        RuntimeCapabilities capabilities,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        IPatchService patchService)
    {
        if (!capabilities.CanRunAgentTasks)
        {
            if (executionMode == AgentExecutionMode.Review)
            {
                return await RunLocalReviewAsync(renderer, workspace, capabilities, patchService);
            }

            renderer.WriteError(
                $"Agent task execution is unavailable in '{capabilities.ModeLabel}' mode. Run 'agent doctor' to inspect gateway connectivity and environment restrictions.");

            return new AgentRunResult(
                false,
                $"Task was not started because model execution is unavailable. {capabilities.ModelStatus}.",
                0,
                "not-started",
                Array.Empty<SessionStep>());
        }

        using var observer = new SpinnerObserver(renderer);

        renderer.WritePanel("Task", task);

        var result = await loop.RunAsync(new AgentRunRequest(
            task,
            workspace,
            profile,
            executionMode,
            approvalMode,
            null,
            observer),
            CancellationToken.None);

        await observer.WriteActivitySummaryAsync(workspace, CancellationToken.None);

        renderer.WritePanel(
            result.Success ? "Done" : "Incomplete",
            $"Session: {result.SessionId}\nSteps: {result.Steps}\n\n{result.FinalMessage}");

        return result;
    }

    private static string BuildReviewTask(string? suffix)
    {
        var baseTask = "Review current workspace changes. Prefer git_diff if git is available; otherwise use workspace_snapshot_diff. Prioritize bugs, regressions, risky behavior changes, and missing tests.";
        return string.IsNullOrWhiteSpace(suffix) ? baseTask : baseTask + "\nFocus: " + suffix.Trim();
    }

    private static async Task<AgentRunResult> RunLocalReviewAsync(
        AnsiRenderer renderer,
        string workspace,
        RuntimeCapabilities capabilities,
        IPatchService patchService)
    {
        var summary = new StringBuilder();
        if (capabilities.GitAvailable)
        {
            var status = await ProcessRunner.RunAsync("git", new[] { "status", "--short", "--branch" }, workspace, CancellationToken.None, 32 * 1024);
            var diff = await ProcessRunner.RunAsync("git", new[] { "diff", "--stat" }, workspace, CancellationToken.None, 32 * 1024);
            summary.AppendLine("git status:");
            summary.AppendLine(string.IsNullOrWhiteSpace(status.StdOut) ? "<empty>" : status.StdOut.Trim());
            summary.AppendLine();
            summary.AppendLine("git diff --stat:");
            summary.AppendLine(string.IsNullOrWhiteSpace(diff.StdOut) ? "<empty>" : diff.StdOut.Trim());
        }
        else
        {
            var snapshotResult = await new WorkspaceSnapshotDiffTool().ExecuteAsync(
                new ToolCall("workspace_snapshot_diff", default, "local review"),
                new ToolContext(
                    workspace,
                    "local-review",
                    "review",
                    AgentExecutionMode.Review,
                    ApprovalPolicyMode.ReadOnly,
                    new AgentConfig(),
                    new HybridSearchService(new DisabledModelClientRouter("disabled"), new AgentConfig(), workspace),
                    capabilities,
                    patchService,
                    NullEventLog.Instance),
                CancellationToken.None);

            summary.AppendLine(snapshotResult.Message);
            if (!string.IsNullOrWhiteSpace(snapshotResult.StdOut))
            {
                summary.AppendLine(snapshotResult.StdOut);
            }
        }

        renderer.WritePanel("Review", summary.ToString().TrimEnd());
        return new AgentRunResult(true, "Local review summary generated without model execution.", 0, "local-review", Array.Empty<SessionStep>());
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
