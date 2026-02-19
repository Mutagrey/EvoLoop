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
            var workspace = Path.GetFullPath(command.Workspace ?? Directory.GetCurrentDirectory());
            var config = BuildEffectiveConfig(AgentConfigLoader.LoadOrCreate(command.ConfigPath), command);

            var useColor = command.NoColor ? false : config.Ui.UseColor;
            var renderer = new AnsiRenderer(useColor);

            if (config.Safety.OfflineStrictMode)
            {
                renderer.WriteWarn("Offline strict mode is ON. Network shell commands are blocked except approved gateway hosts.");
            }

            if (!HasApiAuthConfigured(config))
            {
                renderer.WriteWarn(
                    $"API auth is not configured. Set env var '{config.Api.ApiKeyEnvVar}', or set api.apiKey, or configure auth headers in config.");
            }

            using var modelRouter = new ModelClientRouter(config);
            var searchService = new HybridSearchService(modelRouter, config, workspace);
            var contextFactory = new DefaultToolContextFactory(config, searchService);
            var tools = ToolCatalog.CreateDefaultTools();
            var policy = new DefaultPolicyEngine(config);
            var approval = new ConsoleApprovalService(renderer);
            var eventStore = new HybridEventStore(workspace);
            var loop = new ReActAgentLoop(modelRouter, tools, policy, approval, eventStore, contextFactory, config);

            if (command.Mode == CliMode.Run)
            {
                if (string.IsNullOrWhiteSpace(command.Task))
                {
                    renderer.WriteError("Missing task. Usage: agent run \"your task\" [--profile reasoning|fast|fallback]");
                    return 2;
                }

                var result = await RunTaskAsync(loop, renderer, command.Task, workspace, command.Profile);
                return result.Success ? 0 : 1;
            }

            await RunReplAsync(loop, tools, renderer, config, workspace, command.Profile);
            return 0;
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
        string profile)
    {
        renderer.WriteHeader("EvoLoop Agent CLI");
        renderer.WriteInfo($"Workspace: {workspace}");
        renderer.WriteInfo($"Profile: {profile}");
        renderer.WriteInfo("Type '/task <your request>' to run. '/exit' to quit.");

        AgentRunResult? lastRun = null;

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

                lastRun = await RunTaskAsync(loop, renderer, task, workspace, profile);
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
                    $"Path: {configPath}\nProfiles: {models}\nAPI URL: {config.Api.BaseUrl}\nOpenAI Path: {config.Api.OpenAiCompatiblePath}\nCustom Path: {config.Api.CustomPath}\nSystemPromptMode: {config.Api.SystemPromptMode}\nSystemPromptFallbackToUserMessage: {config.Api.SystemPromptFallbackToUserMessage}\nApiKeyEnvVar: {config.Api.ApiKeyEnvVar}\nApiKey: {apiKeyState} ({apiKeySource})\nOfflineStrict: {config.Safety.OfflineStrictMode}\nAllowedHosts: {hosts}");
                continue;
            }

            if (input.Equals("/approve", StringComparison.OrdinalIgnoreCase) || input.Equals("/deny", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WriteInfo("Approvals are handled inline when a risky action is requested.");
                continue;
            }

            lastRun = await RunTaskAsync(loop, renderer, input, workspace, profile);
        }

        renderer.WriteInfo("Goodbye.");
    }

    private static async Task<AgentRunResult> RunTaskAsync(
        IAgentLoop loop,
        AnsiRenderer renderer,
        string task,
        string workspace,
        string profile)
    {
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
    Run
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
            Console.Write("approve> ");
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
            case AgentRunEventType.ModelCallStarted:
                StartSpinner(evt.Step, evt.Message);
                break;
            case AgentRunEventType.ModelCallCompleted:
                StopSpinner();
                _renderer.WriteStatus("MODEL", evt.Message, ConsoleColor.Blue);
                break;
            case AgentRunEventType.ModelProfileSwitched:
                _renderer.WriteStatus("MODEL-SWITCH", evt.Message, ConsoleColor.Cyan);
                break;
            case AgentRunEventType.ModelDecisionRecovered:
                _renderer.WriteStatus("RECOVER", evt.Message, ConsoleColor.Cyan);
                break;
            case AgentRunEventType.ModelResponseInvalid:
                _renderer.WriteStatus("WARN", evt.Message, ConsoleColor.Yellow);
                break;
            case AgentRunEventType.FinalRejectedRequiresTool:
                _renderer.WriteStatus("WARN", evt.Message, ConsoleColor.Yellow);
                break;
            case AgentRunEventType.ToolDecision:
                _renderer.WriteStatus("PLAN", evt.Message, ConsoleColor.Magenta);
                break;
            case AgentRunEventType.PolicyDenied:
                _renderer.WriteStatus("DENY", evt.Message, ConsoleColor.Red);
                break;
            case AgentRunEventType.ApprovalRequired:
                _renderer.WriteStatus("APPROVAL", evt.Message, ConsoleColor.Yellow);
                break;
            case AgentRunEventType.ApprovalGranted:
                _renderer.WriteStatus("APPROVAL", "Approved", ConsoleColor.Green);
                break;
            case AgentRunEventType.ApprovalRejected:
                _renderer.WriteStatus("APPROVAL", "Rejected", ConsoleColor.Red);
                break;
            case AgentRunEventType.ToolExecutionStarted:
                _renderer.WriteStatus("RUN", evt.Message, ConsoleColor.Cyan);
                break;
            case AgentRunEventType.ToolExecutionCompleted:
                _renderer.WriteStatus(
                    "RESULT",
                    evt.Message,
                    evt.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Red : ConsoleColor.Green);
                TrackActivity(evt.Message);
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
            sb.AppendLine("Edited");
            foreach (var file in _editedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (diffStats.TryGetValue(file, out var stat))
                {
                    sb.AppendLine($"{file}  +{stat.Added} -{stat.Deleted}");
                }
                else
                {
                    sb.AppendLine(file);
                }
            }
            sb.AppendLine();
        }

        if (_exploreNotes.Count > 0)
        {
            sb.AppendLine($"Explored {_exploreNotes.Count} item(s)");
            foreach (var note in _exploreNotes.TakeLast(6))
            {
                sb.AppendLine(note);
            }
            sb.AppendLine();
        }

        if (_ranCommands.Count > 0)
        {
            sb.AppendLine("Ran");
            foreach (var cmd in _ranCommands.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(10))
            {
                sb.AppendLine(cmd);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Timeline");
        foreach (var line in _activityFeed.TakeLast(14))
        {
            sb.AppendLine(line);
        }

        _renderer.WritePanel("Activity", sb.ToString().TrimEnd());
    }

    private void StartSpinner(int? step, string? hint)
    {
        lock (_sync)
        {
            StopSpinner();
            _spinnerCts = new CancellationTokenSource();
            var token = _spinnerCts.Token;
            var hintText = string.IsNullOrWhiteSpace(hint) ? "Thinking" : ToOneLine(hint, 60);
            var label = step.HasValue ? $"Thinking (step {step.Value}) {hintText}" : $"Thinking {hintText}";
            _spinnerTask = Task.Run(async () =>
            {
                var frames = new[] { "|", "/", "-", "\\" };
                var index = 0;
                while (!token.IsCancellationRequested)
                {
                    Console.Write($"\r{label} {frames[index++ % frames.Length]}");
                    try
                    {
                        await Task.Delay(120, token);
                    }
                    catch
                    {
                        break;
                    }
                }

                Console.Write("\r" + new string(' ', Math.Max(20, label.Length + 4)) + "\r");
            }, token);
        }
    }

    private void StopSpinner()
    {
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
    private readonly bool _useColor;

    public AnsiRenderer(bool useColor)
    {
        _useColor = useColor;
    }

    public void WriteHeader(string title)
    {
        var line = new string('=', Math.Max(10, title.Length + 8));
        WriteRaw(Colorize(line, ConsoleColor.Cyan));
        WriteRaw(Colorize($"   {title}", ConsoleColor.Cyan));
        WriteRaw(Colorize(line, ConsoleColor.Cyan));
    }

    public void WritePanel(string title, string body)
    {
        var border = "+" + new string('-', Math.Max(12, title.Length + 2)) + "+";
        WriteRaw(Colorize(border, ConsoleColor.DarkGray));
        WriteRaw(Colorize($"| {title} |", ConsoleColor.White));
        WriteRaw(Colorize(border, ConsoleColor.DarkGray));
        WriteRaw(body);
    }

    public void WriteStatus(string tag, string message, ConsoleColor color)
    {
        WriteRaw($"[{Colorize(tag, color)}] {message}");
    }

    public void WriteInfo(string message) => WriteRaw(Colorize(message, ConsoleColor.Gray));
    public void WriteWarn(string message) => WriteRaw(Colorize(message, ConsoleColor.Yellow));
    public void WriteError(string message) => WriteRaw(Colorize(message, ConsoleColor.Red));

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
}
