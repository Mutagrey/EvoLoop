using Agent.Core;
using Agent.Hosting;

namespace Agent.Tui;

internal sealed class TuiApp
{
    private readonly object _sync = new();
    private readonly List<TuiMessage> _messages = new();
    private string _activity = "idle";
    private ITuiTaskRunner? _taskRunner;
    private AgentRunResult? _lastRun;
    private AgentRunResult? _lastPlan;
    private ReviewDiffNavigator? _lastReviewDiff;
    private bool _taskRunning;
    private bool _modelThinking;
    private CancellationTokenSource? _taskCancellation;
    private Func<ApprovalRequest, CancellationToken, Task<bool>>? _approvalPrompt;
    private Func<TuiChoiceMenuRequest, CancellationToken, Task<string?>>? _choicePrompt;
    private Func<CancellationToken, Task<TuiRuntimeInfo>>? _configReload;
    private IConfigFileOpener _configFileOpener = new DefaultConfigFileOpener();

    public TuiApp(TuiRuntimeInfo runtime, SlashCommandRegistry commands)
    {
        Runtime = runtime;
        Commands = commands;
        AddStartupMessages();
    }

    public TuiRuntimeInfo Runtime { get; private set; }
    public SlashCommandRegistry Commands { get; }
    public event Action? Changed;
    public IReadOnlyList<TuiMessage> Messages
    {
        get
        {
            lock (_sync)
            {
                return _messages.ToList();
            }
        }
    }

    public bool ExitRequested { get; private set; }
    public bool IsTaskRunning
    {
        get
        {
            lock (_sync)
            {
                return _taskRunning;
            }
        }
    }

    public bool IsModelThinking
    {
        get
        {
            lock (_sync)
            {
                return _modelThinking;
            }
        }
    }

    public string Header =>
        $"EvoLoop Agent | profile: {Runtime.Profile} | mode: {Runtime.ModeLabel}";

    public string StatusLine
    {
        get
        {
            lock (_sync)
            {
                return $"{_activity} | approval: {Runtime.ApprovalMode} | theme: {Runtime.ThemeName}";
            }
        }
    }

    public string Transcript(int maxWidth = 100)
    {
        lock (_sync)
        {
            return TranscriptRenderer.Render(_messages.ToList(), maxWidth);
        }
    }

    public void AttachTaskRunner(ITuiTaskRunner taskRunner)
    {
        _taskRunner = taskRunner;
    }

    public void AttachApprovalPrompt(Func<ApprovalRequest, CancellationToken, Task<bool>> approvalPrompt)
    {
        _approvalPrompt = approvalPrompt;
    }

    public void AttachChoicePrompt(Func<TuiChoiceMenuRequest, CancellationToken, Task<string?>> choicePrompt)
    {
        _choicePrompt = choicePrompt;
    }

    public void AttachConfigReload(Func<CancellationToken, Task<TuiRuntimeInfo>> configReload)
    {
        _configReload = configReload;
    }

    public void AttachConfigFileOpener(IConfigFileOpener opener)
    {
        _configFileOpener = opener;
    }

    public bool CancelRunningTask()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_taskRunning || _taskCancellation is null || _taskCancellation.IsCancellationRequested)
            {
                return false;
            }

            _activity = "cancelling";
            _messages.Add(TuiMessage.Status("cancelling current task"));
            cancellation = _taskCancellation;
        }

        cancellation.Cancel();
        Changed?.Invoke();
        return true;
    }

    public TuiCommandResult Submit(string input)
    {
        return SubmitAsync(input, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<TuiCommandResult> SubmitAsync(string input, CancellationToken ct)
    {
        var text = input.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TuiCommandResult(false, false, false, string.Empty);
        }

        AddMessage(TuiMessage.User(text));

        if (TryParseTaskInput(text, Runtime.ApprovalMode, out var task, out var executionMode, out var approvalMode))
        {
            return await RunTaskAsync(task, executionMode, approvalMode, ct);
        }

        if (text.Equals("/plan", StringComparison.OrdinalIgnoreCase))
        {
            var message = _lastPlan is null
                ? "No plan has been generated yet."
                : _lastPlan.FinalMessage;
            AddMessage(_lastPlan is null ? TuiMessage.Status(message) : TuiMessage.Assistant(message));
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            var message = _lastRun is null
                ? "No task executed yet."
                : $"Session: {_lastRun.SessionId}; success={_lastRun.Success}; steps={_lastRun.Steps}";
            AddStatus(message);
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            return ClearTranscript();
        }

        if (text.Equals("/memory", StringComparison.OrdinalIgnoreCase))
        {
            var message = Runtime.MemoryEnabled
                ? TuiStorageInspector.FormatMemory(Runtime.Workspace)
                : "Memory is disabled in runtime config.";
            AddMessage(TuiMessage.System(message));
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/compact", StringComparison.OrdinalIgnoreCase))
        {
            return CompactVisibleContext();
        }

        if (TryHandleSessionsCommand(text, out var sessionsResult))
        {
            return sessionsResult;
        }

        if (TryHandleStorageCommand(text, out var storageResult))
        {
            return storageResult;
        }

        if (TryHandleDiffCommand(text, out var diffResult))
        {
            return diffResult;
        }

        if (text.Equals("/model", StringComparison.OrdinalIgnoreCase))
        {
            return await PickModelProfileAsync(ct);
        }

        if (text.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            var argument = text[7..].Trim();
            if (argument.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                var message = FormatModel();
                AddMessage(TuiMessage.System(message));
                return new TuiCommandResult(true, false, false, message);
            }

            return SwitchModelProfile(argument);
        }

        if (text.Equals("/models", StringComparison.OrdinalIgnoreCase))
        {
            var message = FormatModels();
            AddMessage(TuiMessage.System(message));
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/skills", StringComparison.OrdinalIgnoreCase))
        {
            var message = TuiSkillsFormatter.Format(Runtime.Workspace);
            AddMessage(TuiMessage.System(message));
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/config", StringComparison.OrdinalIgnoreCase))
        {
            var message = TuiConfigFormatter.Format(Runtime);
            AddMessage(TuiMessage.System(message));
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/config path", StringComparison.OrdinalIgnoreCase))
        {
            var message = TuiConfigFormatter.FormatPath(Runtime);
            AddStatus(message);
            return new TuiCommandResult(true, false, false, message);
        }

        if (text.Equals("/config open", StringComparison.OrdinalIgnoreCase))
        {
            var result = _configFileOpener.Open(Runtime.ConfigPath);
            AddMessage(result.Success ? TuiMessage.Status(result.Message) : TuiMessage.Error(result.Message));
            return new TuiCommandResult(true, false, !result.Success, result.Message);
        }

        if (text.Equals("/config reload", StringComparison.OrdinalIgnoreCase))
        {
            return await ReloadConfigAsync(ct);
        }

        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            var result = Commands.Execute(text);
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                AddMessage(result.IsError ? TuiMessage.Error(result.Message) : TuiMessage.System(result.Message));
            }

            ExitRequested = result.ExitRequested;
            return result;
        }

        return await RunTaskAsync(text, AgentExecutionMode.Run, Runtime.ApprovalMode, ct);
    }

    public void RecordRuntimeEvent(AgentRunEvent evt)
    {
        var message = TuiRuntimeEventFormatter.Format(evt);
        lock (_sync)
        {
            _modelThinking = evt.Type switch
            {
                AgentRunEventType.ModelCallStarted => true,
                AgentRunEventType.ModelCallCompleted => false,
                AgentRunEventType.ToolExecutionStarted => false,
                AgentRunEventType.ApprovalRequired => false,
                AgentRunEventType.SessionCompleted => false,
                AgentRunEventType.Error => false,
                _ => _modelThinking
            };
            _activity = message.Content;
            _messages.Add(message);
        }

        Changed?.Invoke();
    }

    public void RecordApprovalRequest(ApprovalRequest request)
    {
        var message = $"approval required: {request.ToolName} - {request.Reason}";
        lock (_sync)
        {
            _activity = message;
            _messages.Add(TuiMessage.Status(message));
        }

        Changed?.Invoke();
    }

    public void RecordApprovalResult(string toolName, bool approved)
    {
        var message = approved ? $"approved: {toolName}" : $"rejected: {toolName}";
        lock (_sync)
        {
            _activity = message;
            _messages.Add(TuiMessage.Status(message));
        }

        Changed?.Invoke();
    }

    public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var prompt = _approvalPrompt;
        return prompt is null ? Task.FromResult(false) : prompt(request, ct);
    }

    private void AddStartupMessages()
    {
        AddMessage(TuiMessage.System("TUI shell ready. Type a task, /plan <task>, /review [focus], /diff, /model, /skills, /sessions, /storage, /compact, /status, /help, or /exit."));
        AddStatus($"Workspace: {Runtime.Workspace}");
        AddStatus($"Model profile: {Runtime.Profile}; model: {Runtime.ModelId}; runtime mode: {Runtime.ModeLabel}");

        if (!Runtime.Workspace.Equals(Runtime.RequestedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            AddStatus($"Workspace resolved to git root: {Runtime.Workspace}");
        }

        if (Runtime.OfflineStrict)
        {
            AddStatus("Offline strict mode is on.");
        }

        if (!Runtime.ApiAuthConfigured)
        {
            AddStatus("API auth is not configured.");
        }

        if (!Runtime.CanRunAgentTasks)
        {
            AddStatus($"Model execution unavailable: {Runtime.ModelStatus}");
        }
    }

    private async Task<TuiCommandResult> ReloadConfigAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_taskRunning)
            {
                const string busy = "Cannot reload config while a task is running.";
                _messages.Add(TuiMessage.Error(busy));
                Changed?.Invoke();
                return new TuiCommandResult(false, false, true, busy);
            }
        }

        var reload = _configReload;
        if (reload is null)
        {
            const string notConfigured = "Config reload is not attached.";
            AddMessage(TuiMessage.Error(notConfigured));
            return new TuiCommandResult(false, false, true, notConfigured);
        }

        try
        {
            var updated = await reload(ct);
            Runtime = updated;
            var message = $"Config reloaded from {Runtime.ConfigPath}. Model profile: {Runtime.Profile}; mode: {Runtime.ModeLabel}.";
            AddStatus(message);
            return new TuiCommandResult(true, false, false, message);
        }
        catch (Exception ex)
        {
            var message = $"Config reload failed: {ex.Message}";
            AddMessage(TuiMessage.Error(message));
            return new TuiCommandResult(false, false, true, message);
        }
    }

    private void AddStatus(string content)
    {
        AddMessage(TuiMessage.Status(content));
    }

    private void AddMessage(TuiMessage message)
    {
        lock (_sync)
        {
            _messages.Add(message);
        }

        Changed?.Invoke();
    }

    private TuiCommandResult ClearTranscript()
    {
        const string message = "Transcript cleared. Session history on disk is unchanged.";
        lock (_sync)
        {
            _messages.Clear();
            _messages.Add(TuiMessage.Status(message));
            _activity = "idle";
            _modelThinking = false;
        }

        Changed?.Invoke();
        return new TuiCommandResult(true, false, false, message);
    }

    private TuiCommandResult CompactVisibleContext()
    {
        IReadOnlyList<TuiMessage> snapshot;
        lock (_sync)
        {
            if (_taskRunning)
            {
                const string busy = "Cannot compact while a task is running. Current runtime history cannot be changed mid-run.";
                _messages.Add(TuiMessage.Error(busy));
                Changed?.Invoke();
                return new TuiCommandResult(false, false, true, busy);
            }

            snapshot = _messages.ToList();
        }

        try
        {
            var compact = TuiStorageInspector.Compact(Runtime, snapshot, _lastRun, _lastPlan);
            AddMessage(compact.Success ? TuiMessage.Status(compact.Message) : TuiMessage.Error(compact.Message));
            return new TuiCommandResult(true, false, !compact.Success, compact.Message);
        }
        catch (Exception ex)
        {
            var message = $"Compact failed: {ex.Message}";
            AddMessage(TuiMessage.Error(message));
            return new TuiCommandResult(false, false, true, message);
        }
    }

    private async Task<TuiCommandResult> RunTaskAsync(
        string task,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        CancellationToken ct)
    {
        if (_taskRunner is null)
        {
            const string notReady = "Agent runtime is not attached.";
            AddMessage(TuiMessage.Error(notReady));
            return new TuiCommandResult(false, false, true, notReady);
        }

        var busy = false;
        CancellationTokenSource? runCancellation = null;
        lock (_sync)
        {
            if (_taskRunning)
            {
                busy = true;
            }
            else
            {
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _taskCancellation = runCancellation;
                _taskRunning = true;
                _activity = executionMode == AgentExecutionMode.Plan ? "planning" : executionMode == AgentExecutionMode.Review ? "reviewing" : "running";
                _messages.Add(TuiMessage.Status($"{_activity}: {task}"));
            }
        }

        if (busy)
        {
            const string message = "A task is already running.";
            AddMessage(TuiMessage.Error(message));
            return new TuiCommandResult(false, false, true, message);
        }

        Changed?.Invoke();

        try
        {
            var observer = new TuiRuntimeObserver(this);
            var outcome = await _taskRunner.RunAsync(
                task,
                Runtime.Profile,
                executionMode,
                approvalMode,
                observer,
                runCancellation?.Token ?? ct);
            var result = outcome.Result;
            if (executionMode == AgentExecutionMode.Plan)
            {
                _lastPlan = result;
            }
            else
            {
                _lastRun = result;
            }

            var body = outcome.LocalReviewSummary ?? $"Session: {result.SessionId}\nSteps: {result.Steps}\n\n{result.FinalMessage}";
            AddMessage(result.Success ? TuiMessage.Assistant(body) : TuiMessage.Error(body));
            if (executionMode == AgentExecutionMode.Review)
            {
                StoreReviewDiff(outcome.LocalReviewSummary ?? result.FinalMessage);
            }

            return new TuiCommandResult(true, false, !result.Success, result.FinalMessage);
        }
        catch (OperationCanceledException) when (runCancellation?.IsCancellationRequested == true || ct.IsCancellationRequested)
        {
            const string message = "Task cancelled.";
            AddMessage(TuiMessage.Status(message));
            return new TuiCommandResult(true, false, true, message);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_taskCancellation, runCancellation))
                {
                    _taskCancellation = null;
                }

                _taskRunning = false;
                _modelThinking = false;
                _activity = "idle";
            }

            runCancellation?.Dispose();
            Changed?.Invoke();
        }
    }

    private static bool TryParseTaskInput(
        string text,
        ApprovalPolicyMode defaultApprovalMode,
        out string task,
        out AgentExecutionMode executionMode,
        out ApprovalPolicyMode approvalMode)
    {
        task = string.Empty;
        executionMode = AgentExecutionMode.Run;
        approvalMode = defaultApprovalMode;

        if (text.StartsWith("/task ", StringComparison.OrdinalIgnoreCase))
        {
            task = text[6..].Trim();
            return !string.IsNullOrWhiteSpace(task);
        }

        if (text.StartsWith("/plan ", StringComparison.OrdinalIgnoreCase))
        {
            task = text[6..].Trim();
            executionMode = AgentExecutionMode.Plan;
            approvalMode = ApprovalPolicyMode.ReadOnly;
            return !string.IsNullOrWhiteSpace(task);
        }

        if (text.StartsWith("/review", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = text.Length > 7 ? text[7..].Trim() : null;
            task = AgentTaskRunner.BuildReviewTask(suffix);
            executionMode = AgentExecutionMode.Review;
            approvalMode = ApprovalPolicyMode.ReadOnly;
            return true;
        }

        return false;
    }

    private bool TryHandleDiffCommand(string text, out TuiCommandResult result)
    {
        result = new TuiCommandResult(false, false, false, string.Empty);
        if (!text.Equals("/diff", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("/diff ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var args = text.Length > 5 ? text[5..].Trim() : string.Empty;
        var navigator = _lastReviewDiff;
        if (navigator is null || !navigator.HasFiles)
        {
            const string message = "No review diff is available yet. Run /review first.";
            AddMessage(TuiMessage.Status(message));
            result = new TuiCommandResult(true, false, false, message);
            return true;
        }

        string rendered;
        var isError = false;
        if (string.IsNullOrWhiteSpace(args))
        {
            rendered = navigator.RenderCurrent();
        }
        else if (args.Equals("files", StringComparison.OrdinalIgnoreCase))
        {
            rendered = navigator.RenderFiles();
        }
        else if (args.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            rendered = navigator.Next();
        }
        else if (args.Equals("prev", StringComparison.OrdinalIgnoreCase) ||
                 args.Equals("previous", StringComparison.OrdinalIgnoreCase))
        {
            rendered = navigator.Previous();
        }
        else if (int.TryParse(args, out var index))
        {
            isError = !navigator.TrySelect(index, out rendered);
        }
        else
        {
            rendered = "Usage: /diff, /diff files, /diff next, /diff prev, or /diff <number>.";
            isError = true;
        }

        AddMessage(isError ? TuiMessage.Error(rendered) : TuiMessage.System(rendered));
        result = new TuiCommandResult(true, false, isError, rendered);
        return true;
    }

    private bool TryHandleSessionsCommand(string text, out TuiCommandResult result)
    {
        result = new TuiCommandResult(false, false, false, string.Empty);
        if (text.Equals("/sessions", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/sessions ", StringComparison.OrdinalIgnoreCase))
        {
            var countText = text.Length > 9 ? text[9..].Trim() : string.Empty;
            var count = int.TryParse(countText, out var parsed) ? parsed : 20;
            var message = TuiStorageInspector.FormatSessions(Runtime.Workspace, count);
            AddMessage(TuiMessage.System(message));
            result = new TuiCommandResult(true, false, false, message);
            return true;
        }

        if (text.StartsWith("/session ", StringComparison.OrdinalIgnoreCase))
        {
            var id = text[9..].Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                const string usage = "Usage: /session <id>";
                AddMessage(TuiMessage.Error(usage));
                result = new TuiCommandResult(true, false, true, usage);
                return true;
            }

            var message = TuiStorageInspector.FormatSession(Runtime.Workspace, id);
            var isError = message.StartsWith("Session not found", StringComparison.OrdinalIgnoreCase) ||
                          message.StartsWith("Session id is ambiguous", StringComparison.OrdinalIgnoreCase);
            AddMessage(isError ? TuiMessage.Error(message) : TuiMessage.System(message));
            result = new TuiCommandResult(true, false, isError, message);
            return true;
        }

        return false;
    }

    private bool TryHandleStorageCommand(string text, out TuiCommandResult result)
    {
        result = new TuiCommandResult(false, false, false, string.Empty);
        if (!text.Equals("/storage", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("/storage ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var args = text.Length > 8 ? text[8..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(args))
        {
            var message = TuiStorageInspector.FormatStorage(Runtime.Workspace);
            AddMessage(TuiMessage.System(message));
            result = new TuiCommandResult(true, false, false, message);
            return true;
        }

        TuiStorageCommandResult storageResult;
        if (args.Equals("archive", StringComparison.OrdinalIgnoreCase))
        {
            storageResult = TuiStorageInspector.Archive(Runtime.Workspace);
        }
        else if (args.StartsWith("prune", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseKeepCount(args, out var keep))
            {
                const string usage = "Usage: /storage prune --keep N";
                AddMessage(TuiMessage.Error(usage));
                result = new TuiCommandResult(true, false, true, usage);
                return true;
            }

            storageResult = TuiStorageInspector.Prune(Runtime.Workspace, keep);
        }
        else
        {
            const string usage = "Usage: /storage, /storage archive, or /storage prune --keep N";
            AddMessage(TuiMessage.Error(usage));
            result = new TuiCommandResult(true, false, true, usage);
            return true;
        }

        AddMessage(storageResult.Success ? TuiMessage.Status(storageResult.Message) : TuiMessage.Error(storageResult.Message));
        result = new TuiCommandResult(true, false, !storageResult.Success, storageResult.Message);
        return true;
    }

    private static bool TryParseKeepCount(string args, out int keep)
    {
        keep = 0;
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("--keep", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            {
                return int.TryParse(parts[i + 1], out keep) && keep > 0;
            }
        }

        return false;
    }

    private void StoreReviewDiff(string? reviewSummary)
    {
        _lastReviewDiff = ReviewDiffNavigator.FromReviewSummary(reviewSummary);
        if (!_lastReviewDiff.HasFiles)
        {
            AddStatus("No navigable diff was found in the review output.");
            return;
        }

        AddStatus($"Review diff ready: {_lastReviewDiff.Count} files. Use /diff, /diff files, /diff next, /diff prev, or /diff <number>.");
    }

    private string FormatModel()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "Model",
            $"|- active profile: {Runtime.Profile}",
            $"|- provider: {Runtime.ModelProvider}",
            $"|- model: {Runtime.ModelId}",
            $"|- tool calling: {Runtime.ToolCallingMode}",
            $"|- runtime mode: {Runtime.ModeLabel}",
            $"|- gateway: {(Runtime.ModelReachable ? "reachable" : "unreachable")}",
            $"|- auth: {(Runtime.ApiAuthConfigured ? "present" : "missing")}",
            $"`- status: {Runtime.ModelStatus}"
        });
    }

    private async Task<TuiCommandResult> PickModelProfileAsync(CancellationToken ct)
    {
        var prompt = _choicePrompt;
        if (prompt is null)
        {
            const string unavailable = "Model picker is not attached. Use /model status or /model <profile>.";
            AddMessage(TuiMessage.Error(unavailable));
            return new TuiCommandResult(false, false, true, unavailable);
        }

        var request = new TuiChoiceMenuRequest(
            "Select model profile",
            "Selection applies to this TUI session only. Config is not changed.",
            BuildModelChoiceItems(),
            Runtime.Profile);
        var selected = await prompt(request, ct);
        if (string.IsNullOrWhiteSpace(selected))
        {
            const string cancelled = "Model selection cancelled.";
            AddStatus(cancelled);
            return new TuiCommandResult(true, false, false, cancelled);
        }

        return SwitchModelProfile(selected);
    }

    private TuiCommandResult SwitchModelProfile(string profileName)
    {
        var profile = FindModelProfile(profileName);
        if (profile is null)
        {
            var configured = Runtime.ModelProfiles.Count == 0
                ? "<none>"
                : string.Join(", ", Runtime.ModelProfiles);
            var message = $"Unknown model profile: {profileName}. Configured profiles: {configured}.";
            AddMessage(TuiMessage.Error(message));
            return new TuiCommandResult(false, false, true, message);
        }

        Runtime = Runtime with
        {
            Profile = profile.Name,
            ModelProvider = profile.Provider,
            ModelId = profile.ModelId,
            ToolCallingMode = profile.ToolCallingMode
        };
        var changed = $"Model profile switched to {profile.Name} ({profile.ModelId}) for this TUI session.";
        AddStatus(changed);
        return new TuiCommandResult(true, false, false, changed);
    }

    private IReadOnlyList<ChoiceMenuItem> BuildModelChoiceItems()
    {
        var details = Runtime.ModelProfileDetails.Count == 0
            ? Runtime.ModelProfiles
                .Select(name => new TuiModelProfileInfo(name, Runtime.ModelProvider, Runtime.ModelId, Runtime.ToolCallingMode))
                .ToArray()
            : Runtime.ModelProfileDetails;

        return details
            .Select(profile => new ChoiceMenuItem(
                profile.Name,
                profile.Name,
                $"{profile.Provider}; model {profile.ModelId}; tools {profile.ToolCallingMode}",
                profile.Name.Equals(Runtime.Profile, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private TuiModelProfileInfo? FindModelProfile(string profileName)
    {
        return Runtime.ModelProfileDetails.FirstOrDefault(profile =>
                   profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) ??
               Runtime.ModelProfiles
                   .Where(profile => profile.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                   .Select(profile => new TuiModelProfileInfo(
                       profile,
                       Runtime.ModelProvider,
                       Runtime.ModelId,
                       Runtime.ToolCallingMode))
                   .FirstOrDefault();
    }

    private string FormatModels()
    {
        IReadOnlyList<string> profiles = Runtime.ModelProfiles.Count == 0
            ? new[] { Runtime.Profile }
            : Runtime.ModelProfiles;
        var lines = new List<string> { "Model profiles" };
        for (var i = 0; i < profiles.Count; i++)
        {
            var marker = i == profiles.Count - 1 ? "`-" : "|-";
            var active = profiles[i].Equals(Runtime.Profile, StringComparison.OrdinalIgnoreCase) ? " (active)" : string.Empty;
            lines.Add($"{marker} {profiles[i]}{active}");
        }

        lines.Add(string.Empty);
        lines.Add("Fallback order");
        if (Runtime.ProfileFallbackOrder.Count == 0)
        {
            lines.Add("`- <none>");
        }
        else
        {
            for (var i = 0; i < Runtime.ProfileFallbackOrder.Count; i++)
            {
                var marker = i == Runtime.ProfileFallbackOrder.Count - 1 ? "`-" : "|-";
                lines.Add($"{marker} {Runtime.ProfileFallbackOrder[i]}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
