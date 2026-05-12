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
    private bool _taskRunning;
    private Func<ApprovalRequest, CancellationToken, Task<bool>>? _approvalPrompt;

    public TuiApp(TuiRuntimeInfo runtime, SlashCommandRegistry commands)
    {
        Runtime = runtime;
        Commands = commands;
        AddStartupMessages();
    }

    public TuiRuntimeInfo Runtime { get; }
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
        AddMessage(TuiMessage.System("TUI shell ready. Type a task, /plan <task>, /review [focus], /status, /help, or /exit."));
        AddStatus($"Workspace: {Runtime.Workspace}");
        AddStatus($"Profile: {Runtime.Profile}; runtime mode: {Runtime.ModeLabel}");

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
        lock (_sync)
        {
            if (_taskRunning)
            {
                busy = true;
            }
            else
            {
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
            var outcome = await _taskRunner.RunAsync(task, Runtime.Profile, executionMode, approvalMode, observer, ct);
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
            return new TuiCommandResult(true, false, !result.Success, result.FinalMessage);
        }
        finally
        {
            lock (_sync)
            {
                _taskRunning = false;
                _activity = "idle";
            }

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
}
