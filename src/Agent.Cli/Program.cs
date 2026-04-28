using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
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
            var renderer = new AnsiRenderer(useColor);
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

            var searchService = new HybridSearchService(modelRouter, config, workspace);
            var contextFactory = new DefaultToolContextFactory(config, searchService, capabilities);
            var tools = ToolCatalog.CreateDefaultTools();
            var policy = new DefaultPolicyEngine(config);
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
                if (command.Mode == CliMode.Run)
                {
                    if (string.IsNullOrWhiteSpace(command.Task))
                    {
                        renderer.WriteError("Missing task. Usage: agent run \"your task\" [--profile reasoning|fast|fallback]");
                        return 2;
                    }

                    var result = await RunTaskAsync(loop, renderer, command.Task, workspace, command.Profile, capabilities);
                    return result.Success ? 0 : 1;
                }

                await RunReplAsync(loop, tools, renderer, config, workspace, command.Profile, memoryStore, capabilities);
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
        RuntimeCapabilities capabilities)
    {
        renderer.WriteHeader("EvoLoop Agent CLI");
        renderer.WritePanel(
            "Session",
            $"Workspace: {workspace}\nProfile: {profile}\nMode: {capabilities.ModeLabel}\nCommands: /task, /status, /tools, /history, /memory, /cmdlog, /config, /doctor, /exit\nRecall: !N");

        AgentRunResult? lastRun = null;
        var commandHistory = await ReplCommandHistory.OpenAsync(workspace, 300, CancellationToken.None);

        while (true)
        {
            Console.Write("\nagent :: ");
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
                lastRun = await RunTaskAsync(loop, renderer, task, workspace, profile, capabilities);
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
                var body = string.Join(Environment.NewLine, tools.OrderBy(t => t.Name).Select(t => $"- {t.Name}: {t.Schema.Description}"));
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
            lastRun = await RunTaskAsync(loop, renderer, input, workspace, profile, capabilities);
        }

        renderer.WriteInfo("Goodbye.");
    }

    private static async Task<AgentRunResult> RunTaskAsync(
        IAgentLoop loop,
        AnsiRenderer renderer,
        string task,
        string workspace,
        string profile,
        RuntimeCapabilities capabilities)
    {
        if (!capabilities.CanRunAgentTasks)
        {
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
            null,
            observer),
            CancellationToken.None);

        await observer.WriteActivitySummaryAsync(workspace, CancellationToken.None);

        renderer.WritePanel(
            result.Success ? "Done" : "Incomplete",
            $"Session: {result.SessionId}\nSteps: {result.Steps}\n\n{result.FinalMessage}");

        return result;
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

internal enum CliMode
{
    Interactive,
    Run,
    Doctor
}

internal sealed class CliArguments
{
    public CliMode Mode { get; init; } = CliMode.Interactive;
    public string? Task { get; init; }
    public string Profile { get; init; } = "reasoning";
    public string? Workspace { get; init; }
    public string? ConfigPath { get; init; }
    public bool NoColor { get; init; }
    public bool OfflineStrict { get; init; }

    public static CliArguments Parse(string[] args)
    {
        var mode = CliMode.Interactive;
        string? task = null;
        var profile = "reasoning";
        string? workspace = null;
        string? configPath = null;
        var noColor = false;
        var offlineStrict = false;

        var i = 0;
        if (args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            mode = CliMode.Run;
            i = 1;
            if (i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                task = args[i];
                i++;
            }
        }
        else if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
        {
            mode = CliMode.Doctor;
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--profile" when i + 1 < args.Length:
                    profile = args[++i];
                    break;
                case "--workspace" when i + 1 < args.Length:
                    workspace = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "--offline-strict":
                    offlineStrict = true;
                    break;
                default:
                    if (mode == CliMode.Run && task is null && !arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        task = arg;
                    }
                    break;
            }
        }

        return new CliArguments
        {
            Mode = mode,
            Task = task,
            Profile = profile,
            Workspace = workspace,
            ConfigPath = configPath,
            NoColor = noColor,
            OfflineStrict = offlineStrict
        };
    }
}

internal sealed class ReplCommandHistory
{
    private readonly string? _path;
    private readonly int _maxEntries;
    private readonly List<string> _entries;

    private ReplCommandHistory(string? path, int maxEntries, List<string> entries)
    {
        _path = path;
        _maxEntries = Math.Max(50, maxEntries);
        _entries = entries;
    }

    public static async Task<ReplCommandHistory> OpenAsync(string workspaceRoot, int maxEntries, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, ".evoloop", "storage", "repl-commands.txt");
        var entries = new List<string>();
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(path))
            {
                var lines = await File.ReadAllLinesAsync(path, ct);
                foreach (var line in lines)
                {
                    var normalized = line.Trim();
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        entries.Add(normalized);
                    }
                }
            }
        }
        catch
        {
            return new ReplCommandHistory(null, maxEntries, entries);
        }

        if (entries.Count > maxEntries)
        {
            entries = entries.Skip(entries.Count - maxEntries).ToList();
        }

        return new ReplCommandHistory(path, maxEntries, entries);
    }

    public bool TryResolve(string token, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        var indexText = token[1..].Trim();
        if (!int.TryParse(indexText, out var index))
        {
            return false;
        }

        if (index < 1 || index > _entries.Count)
        {
            return false;
        }

        command = _entries[index - 1];
        return true;
    }

    public async Task AddAsync(string command, CancellationToken ct)
    {
        var normalized = command.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (_entries.Count > 0 && _entries[^1].Equals(normalized, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(normalized);
        if (_entries.Count > _maxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - _maxEntries);
        }

        if (!string.IsNullOrWhiteSpace(_path))
        {
            await File.WriteAllLinesAsync(_path, _entries, Encoding.UTF8, ct);
        }
    }

    public string FormatRecent(int take)
    {
        if (_entries.Count == 0)
        {
            return "No saved commands yet.";
        }

        var count = Math.Max(1, take);
        var start = Math.Max(0, _entries.Count - count);
        var sb = new StringBuilder();
        for (var i = start; i < _entries.Count; i++)
        {
            sb.AppendLine($"{i + 1,4}: {_entries[i]}");
        }

        sb.Append("Use !N to rerun by index.");
        return sb.ToString();
    }
}

internal sealed class ConsoleApprovalService : IApprovalService
{
    private readonly AnsiRenderer _renderer;

    public ConsoleApprovalService(AnsiRenderer renderer)
    {
        _renderer = renderer;
    }

    public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        _renderer.WritePanel(
            "Approval Required",
            $"Tool: {request.ToolName}\nReason: {request.Reason}\nArguments: {request.ArgumentsPreview}\n\nApprove? (y/N)");

        while (true)
        {
            Console.Write("approve :: ");
            var input = Console.ReadLine();
            if (input is null)
            {
                return Task.FromResult(false);
            }

            input = input.Trim();
            if (input.Equals("y", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(true);
            }

            if (input.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(input))
            {
                return Task.FromResult(false);
            }
        }
    }
}

internal sealed class SpinnerObserver : IAgentRunObserver, IDisposable
{
    private readonly AnsiRenderer _renderer;
    private readonly object _sync = new();
    private readonly List<string> _activityFeed = new();
    private readonly HashSet<string> _editedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ranCommands = new();
    private readonly List<string> _exploreNotes = new();
    private int? _lastAnnouncedStep;
    private DateTimeOffset _spinnerStartedAtUtc;
    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;

    public SpinnerObserver(AnsiRenderer renderer)
    {
        _renderer = renderer;
    }

    public Task OnEventAsync(AgentRunEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case AgentRunEventType.SessionStarted:
                _renderer.WriteStatus("SESSION", evt.Message, ConsoleColor.Cyan);
                break;
            case AgentRunEventType.MemoryLoaded:
                _renderer.WriteStatus("MEMORY", evt.Message, ConsoleColor.DarkCyan);
                break;
            case AgentRunEventType.ContextCompacted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("MEMORY", evt.Message, ConsoleColor.DarkCyan, depth: 1);
                break;
            case AgentRunEventType.ModelCallStarted:
                StartSpinner(evt.Step, evt.Message);
                break;
            case AgentRunEventType.ModelCallCompleted:
                StopSpinner();
                AnnounceStepIfNeeded(evt.Step);
                break;
            case AgentRunEventType.ModelProfileSwitched:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("MODEL", evt.Message, ConsoleColor.Cyan, depth: 1);
                break;
            case AgentRunEventType.ModelDecisionRecovered:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("RECOVER", evt.Message, ConsoleColor.Cyan, depth: 1);
                break;
            case AgentRunEventType.ModelResponseInvalid:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("WARN", evt.Message, ConsoleColor.Yellow, depth: 1);
                break;
            case AgentRunEventType.FinalRejectedRequiresTool:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("WARN", evt.Message, ConsoleColor.Yellow, depth: 1);
                break;
            case AgentRunEventType.ToolDecision:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("PLAN", evt.Message, ConsoleColor.Magenta, depth: 1);
                break;
            case AgentRunEventType.PolicyDenied:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("DENY", evt.Message, ConsoleColor.Red, depth: 2, isLast: true);
                break;
            case AgentRunEventType.ApprovalRequired:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("APPROVAL", evt.Message, ConsoleColor.Yellow, depth: 2);
                break;
            case AgentRunEventType.ApprovalGranted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("APPROVAL", "Approved", ConsoleColor.Green, depth: 2);
                break;
            case AgentRunEventType.ApprovalRejected:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("APPROVAL", "Rejected", ConsoleColor.Red, depth: 2, isLast: true);
                break;
            case AgentRunEventType.ToolExecutionStarted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("RUN", evt.Message, ConsoleColor.Cyan, depth: 2);
                break;
            case AgentRunEventType.ToolExecutionCompleted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus(
                    "RESULT",
                    evt.Message,
                    evt.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Red : ConsoleColor.Green,
                    depth: 2,
                    isLast: true);
                TrackActivity(evt.Message);
                break;
            case AgentRunEventType.MemoryUpdated:
                _renderer.WriteStatus("MEMORY", evt.Message, ConsoleColor.DarkCyan);
                break;
            case AgentRunEventType.SessionCompleted:
                StopSpinner();
                _renderer.WriteStatus("DONE", evt.Message, ConsoleColor.Green);
                break;
            case AgentRunEventType.Error:
                StopSpinner();
                _renderer.WriteStatus("ERROR", evt.Message, ConsoleColor.Red);
                break;
        }

        return Task.CompletedTask;
    }

    private void AnnounceStepIfNeeded(int? step)
    {
        if (!step.HasValue)
        {
            return;
        }

        if (_lastAnnouncedStep.HasValue && _lastAnnouncedStep.Value == step.Value)
        {
            return;
        }

        _lastAnnouncedStep = step.Value;
        _renderer.WriteStatus("STEP", $"Step {step.Value}", ConsoleColor.Cyan);
    }

    public void Dispose()
    {
        StopSpinner();
    }

    public async Task WriteActivitySummaryAsync(string workspace, CancellationToken ct)
    {
        if (_activityFeed.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        var diffStats = await ReadGitNumStatAsync(workspace, _editedFiles, ct);

        if (_editedFiles.Count > 0)
        {
            sb.AppendLine($"Edited ({_editedFiles.Count})");
            foreach (var file in _editedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (diffStats.TryGetValue(file, out var stat))
                {
                    sb.AppendLine($" - {file}  +{stat.Added} -{stat.Deleted}");
                }
                else
                {
                    sb.AppendLine($" - {file}");
                }
            }
            sb.AppendLine();
        }

        if (_exploreNotes.Count > 0)
        {
            sb.AppendLine($"Explored {_exploreNotes.Count} item(s)");
            foreach (var note in _exploreNotes.TakeLast(6))
            {
                sb.AppendLine($" - {note}");
            }
            sb.AppendLine();
        }

        if (_ranCommands.Count > 0)
        {
            sb.AppendLine($"Ran ({_ranCommands.Distinct(StringComparer.OrdinalIgnoreCase).Count()})");
            foreach (var cmd in _ranCommands.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(10))
            {
                sb.AppendLine($" - {cmd}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Timeline (latest)");
        foreach (var line in _activityFeed.TakeLast(14))
        {
            sb.AppendLine($" > {line}");
        }

        _renderer.WritePanel("Activity", sb.ToString().TrimEnd());
    }

    private void StartSpinner(int? step, string? hint)
    {
        if (!_renderer.SupportsTransientOutput)
        {
            return;
        }

        lock (_sync)
        {
            StopSpinner();
            _spinnerCts = new CancellationTokenSource();
            var token = _spinnerCts.Token;
            _spinnerStartedAtUtc = DateTimeOffset.UtcNow;
            var hintText = string.IsNullOrWhiteSpace(hint) ? "Analyzing next action" : ToOneLine(hint, 90);
            var stepToken = TryParseStepProgress(hintText, out var current, out var total)
                ? $"Step {current}/{total} {BuildStepBar(current, total)}"
                : step.HasValue ? $"Step {step.Value}" : "Step";
            _spinnerTask = Task.Run(async () =>
            {
                var frames = new[] { "|", "/", "-", "\\" };
                var index = 0;
                var lastPrintedWidth = 0;
                while (!token.IsCancellationRequested)
                {
                    var elapsed = (DateTimeOffset.UtcNow - _spinnerStartedAtUtc).TotalSeconds;
                    var frame = frames[index++ % frames.Length];
                    var line = $"{stepToken}  {hintText}  {elapsed,5:0.0}s {frame}";
                    line = FitInline(line, GetConsoleWidth() - 1);
                    var padded = line.PadRight(Math.Max(line.Length, lastPrintedWidth));
                    Console.Write($"\r{padded}");
                    lastPrintedWidth = padded.Length;
                    try
                    {
                        await Task.Delay(120, token);
                    }
                    catch
                    {
                        break;
                    }
                }

                Console.Write("\r" + new string(' ', Math.Max(20, lastPrintedWidth)) + "\r");
            }, token);
        }
    }

    private static bool TryParseStepProgress(string text, out int current, out int total)
    {
        current = 0;
        total = 0;
        var match = Regex.Match(text, @"Step\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out current) ||
            !int.TryParse(match.Groups[2].Value, out total) ||
            total <= 0)
        {
            current = 0;
            total = 0;
            return false;
        }

        current = Math.Clamp(current, 0, total);
        return true;
    }

    private static string BuildStepBar(int current, int total)
    {
        const int width = 18;
        if (total <= 0)
        {
            return "[" + new string('-', width) + "]";
        }

        var filled = (int)Math.Round((current / (double)total) * width);
        filled = Math.Clamp(filled, 0, width);
        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Max(60, Console.WindowWidth);
        }
        catch
        {
            return 100;
        }
    }

    private static string FitInline(string text, int width)
    {
        if (width <= 0 || text.Length <= width)
        {
            return text;
        }

        if (width < 4)
        {
            return text[..width];
        }

        return text[..(width - 3)] + "...";
    }

    private void StopSpinner()
    {
        if (!_renderer.SupportsTransientOutput)
        {
            return;
        }

        lock (_sync)
        {
            if (_spinnerCts is null)
            {
                return;
            }

            _spinnerCts.Cancel();
            try
            {
                _spinnerTask?.Wait(200);
            }
            catch
            {
                // ignored
            }

            _spinnerTask = null;
            _spinnerCts.Dispose();
            _spinnerCts = null;
        }
    }

    private void TrackActivity(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = ToOneLine(message, 220);
        _activityFeed.Add(normalized);

        if (normalized.StartsWith("Edited ", StringComparison.OrdinalIgnoreCase))
        {
            var file = NormalizePath(normalized["Edited ".Length..]);
            if (!string.IsNullOrWhiteSpace(file))
            {
                _editedFiles.Add(file);
            }
            return;
        }

        if (normalized.StartsWith("Explored ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Searched ", StringComparison.OrdinalIgnoreCase))
        {
            _exploreNotes.Add(normalized);
            return;
        }

        if (normalized.StartsWith("Ran ", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = normalized["Ran ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                _ranCommands.Add(cmd);
            }
        }
    }

    private static async Task<Dictionary<string, (string Added, string Deleted)>> ReadGitNumStatAsync(
        string workspace,
        IReadOnlyCollection<string> editedFiles,
        CancellationToken ct)
    {
        var stats = new Dictionary<string, (string Added, string Deleted)>(StringComparer.OrdinalIgnoreCase);
        if (editedFiles.Count == 0)
        {
            return stats;
        }

        var args = new List<string> { "diff", "--numstat", "--" };
        foreach (var file in editedFiles)
        {
            args.Add(file);
        }

        ProcessExecutionResult result;
        try
        {
            result = await ProcessRunner.RunAsync("git", args, workspace, ct, 32 * 1024);
        }
        catch
        {
            return stats;
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return stats;
        }

        var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var file = NormalizePath(parts[2].Trim());
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            stats[file] = (parts[0], parts[1]);
        }

        return stats;
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/');
    }

    private static string ToOneLine(string value, int maxLen)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= maxLen)
        {
            return oneLine;
        }

        return oneLine[..maxLen] + "...";
    }
}

internal sealed class AnsiRenderer
{
    private const int MinFrameWidth = 64;
    private const int MaxFrameWidth = 120;
    private const int StatusTagWidth = 12;
    private readonly bool _useColor;
    private readonly bool _usePlainLayout;

    public bool SupportsTransientOutput { get; }

    public AnsiRenderer(bool useColor)
    {
        _useColor = useColor && SupportsAnsiColor();
        _usePlainLayout = OperatingSystem.IsWindows() && !_useColor;
        SupportsTransientOutput = !_usePlainLayout && !Console.IsOutputRedirected;
    }

    public void WriteHeader(string title)
    {
        if (_usePlainLayout)
        {
            WriteRaw("== " + title.ToUpperInvariant() + " ==");
            WriteRaw("Autonomous Coding Agent CLI");
            return;
        }

        var width = GetFrameWidth();
        var top = "+" + new string('=', width - 2) + "+";
        WriteRaw(Colorize(top, ConsoleColor.Cyan));
        WriteFramedLine(title.ToUpperInvariant(), width, ConsoleColor.Cyan);
        WriteFramedLine("Autonomous Coding Agent CLI", width, ConsoleColor.DarkCyan);
        WriteRaw(Colorize(top, ConsoleColor.Cyan));
    }

    public void WritePanel(string title, string body)
    {
        if (_usePlainLayout)
        {
            WriteRaw(string.Empty);
            WriteRaw("[" + TruncateInline(title, 80) + "]");
            foreach (var line in WrapText(body, Math.Max(40, GetFrameWidth() - 2)))
            {
                WriteRaw(line);
            }
            return;
        }

        var width = GetFrameWidth();
        var innerWidth = width - 4;
        var safeTitle = TruncateInline(title, Math.Max(1, innerWidth - 2));
        var top = "+-" + safeTitle + " " + new string('-', Math.Max(1, innerWidth - safeTitle.Length - 1)) + "+";
        var bottom = "+" + new string('-', width - 2) + "+";

        WriteRaw(Colorize(top, ConsoleColor.DarkGray));
        foreach (var line in WrapText(body, innerWidth))
        {
            WriteRaw($"| {line.PadRight(innerWidth)} |");
        }
        WriteRaw(Colorize(bottom, ConsoleColor.DarkGray));
    }

    public void WriteStatus(string tag, string message, ConsoleColor color, int depth = 0, bool isLast = false)
    {
        depth = Math.Max(0, depth);
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var normalizedTag = NormalizeTag(tag);
        var prefixPlain = $"[{timestamp}] [{normalizedTag}] ";
        var prefixColored = _useColor
            ? $"{Colorize($"[{timestamp}]", ConsoleColor.DarkGray)} {Colorize($"[{normalizedTag}]", color)} "
            : prefixPlain;
        var treePrefix = BuildTreePrefix(depth, isLast);
        var treePrefixColored = _useColor && treePrefix.Length > 0
            ? Colorize(treePrefix, ConsoleColor.DarkGray)
            : treePrefix;
        var indent = new string(' ', prefixPlain.Length + treePrefix.Length);
        var maxMessageWidth = Math.Max(24, GetFrameWidth() - prefixPlain.Length - treePrefix.Length - 1);
        var lines = WrapText(message, maxMessageWidth).ToList();
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        WriteRaw(prefixColored + treePrefixColored + lines[0]);
        foreach (var line in lines.Skip(1))
        {
            WriteRaw(indent + line);
        }
    }

    public void WriteInfo(string message) => WriteStatus("INFO", message, ConsoleColor.Gray);
    public void WriteWarn(string message) => WriteStatus("WARN", message, ConsoleColor.Yellow);
    public void WriteError(string message) => WriteStatus("ERROR", message, ConsoleColor.Red);

    private void WriteRaw(string text)
    {
        Console.WriteLine(text);
    }

    private string Colorize(string text, ConsoleColor color)
    {
        if (!_useColor)
        {
            return text;
        }

        var code = color switch
        {
            ConsoleColor.Black => "30",
            ConsoleColor.DarkRed => "31",
            ConsoleColor.DarkGreen => "32",
            ConsoleColor.DarkYellow => "33",
            ConsoleColor.DarkBlue => "34",
            ConsoleColor.DarkMagenta => "35",
            ConsoleColor.DarkCyan => "36",
            ConsoleColor.Gray => "37",
            ConsoleColor.DarkGray => "90",
            ConsoleColor.Red => "91",
            ConsoleColor.Green => "92",
            ConsoleColor.Yellow => "93",
            ConsoleColor.Blue => "94",
            ConsoleColor.Magenta => "95",
            ConsoleColor.Cyan => "96",
            ConsoleColor.White => "97",
            _ => "0"
        };

        return $"\u001b[{code}m{text}\u001b[0m";
    }

    private static bool SupportsAnsiColor()
    {
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION")) ||
            string.Equals(Environment.GetEnvironmentVariable("ConEmuANSI"), "ON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("ANSICON"), "1", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERM")))
        {
            return true;
        }

        return TryEnableVirtualTerminalProcessing();
    }

    private static bool TryEnableVirtualTerminalProcessing()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            if ((mode & EnableVirtualTerminalProcessing) != 0)
            {
                return true;
            }

            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }

    private int GetFrameWidth()
    {
        var width = 100;
        try
        {
            width = Console.WindowWidth > 0 ? Console.WindowWidth : 100;
        }
        catch
        {
            // keep default width
        }

        return Math.Clamp(width, MinFrameWidth, MaxFrameWidth);
    }

    private static string NormalizeTag(string tag)
    {
        var clean = string.IsNullOrWhiteSpace(tag) ? "STATUS" : tag.Trim().ToUpperInvariant();
        if (clean.Length > StatusTagWidth)
        {
            clean = clean[..StatusTagWidth];
        }

        return clean.PadRight(StatusTagWidth);
    }

    private static string BuildTreePrefix(int depth, bool isLast)
    {
        if (depth <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(depth * 3);
        for (var level = 1; level < depth; level++)
        {
            sb.Append("|  ");
        }

        sb.Append(isLast ? "`- " : "|- ");
        return sb.ToString();
    }

    private void WriteFramedLine(string value, int width, ConsoleColor color)
    {
        var innerWidth = width - 4;
        var text = TruncateInline(value, innerWidth);
        var content = text.PadRight(innerWidth);
        WriteRaw(Colorize($"| {content} |", color));
    }

    private static string TruncateInline(string value, int width)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= width)
        {
            return oneLine;
        }

        if (width <= 3)
        {
            return oneLine[..width];
        }

        return oneLine[..(width - 3)] + "...";
    }

    private static IEnumerable<string> WrapText(string? text, int width)
    {
        if (width <= 0)
        {
            yield return string.Empty;
            yield break;
        }

        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rows = normalized.Split('\n');
        foreach (var row in rows)
        {
            var line = row.TrimEnd();
            if (line.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (line.Length > width)
            {
                var take = width;
                var breakAt = line.LastIndexOf(' ', Math.Min(width - 1, line.Length - 1), Math.Min(width, line.Length));
                if (breakAt > 0)
                {
                    take = breakAt;
                }

                yield return line[..take].TrimEnd();
                line = line[take..].TrimStart();
            }

            yield return line;
        }
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
