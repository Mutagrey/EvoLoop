using Agent.Core;

namespace Agent.Tui;

internal sealed class TuiApp
{
    private readonly object _sync = new();
    private readonly List<TuiMessage> _messages = new();
    private string _activity = "idle";

    public TuiApp(TuiRuntimeInfo runtime, SlashCommandRegistry commands)
    {
        Runtime = runtime;
        Commands = commands;
        AddStartupMessages();
    }

    public TuiRuntimeInfo Runtime { get; }
    public SlashCommandRegistry Commands { get; }
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

    public TuiCommandResult Submit(string input)
    {
        var text = input.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TuiCommandResult(false, false, false, string.Empty);
        }

        AddMessage(TuiMessage.User(text));

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

        const string pending = "Agent integration pending. Minimal TUI shell only; use Agent.Cli for run, plan, review, or repl.";
        AddMessage(TuiMessage.System(pending));
        return new TuiCommandResult(true, false, false, pending);
    }

    public void RecordRuntimeEvent(AgentRunEvent evt)
    {
        var text = FormatRuntimeEvent(evt);
        lock (_sync)
        {
            _activity = text;
            _messages.Add(evt.Type switch
            {
                AgentRunEventType.Error => TuiMessage.Error(text),
                AgentRunEventType.SessionCompleted => TuiMessage.Assistant(text),
                AgentRunEventType.PolicyDenied => TuiMessage.Error(text),
                AgentRunEventType.ApprovalRejected => TuiMessage.Error(text),
                _ => TuiMessage.Status(text)
            });
        }
    }

    public void RecordApprovalRequest(ApprovalRequest request)
    {
        var message = $"approval required: {request.ToolName} - {request.Reason}";
        lock (_sync)
        {
            _activity = message;
            _messages.Add(TuiMessage.Status(message));
        }
    }

    public void RecordApprovalResult(string toolName, bool approved)
    {
        var message = approved ? $"approved: {toolName}" : $"rejected: {toolName}";
        lock (_sync)
        {
            _activity = message;
            _messages.Add(TuiMessage.Status(message));
        }
    }

    private void AddStartupMessages()
    {
        AddMessage(TuiMessage.System("Minimal TUI shell ready. Type /help for commands or /exit to quit."));
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
    }

    private static string FormatRuntimeEvent(AgentRunEvent evt)
    {
        var prefix = evt.Step.HasValue ? $"step {evt.Step.Value}: " : string.Empty;
        var tool = string.IsNullOrWhiteSpace(evt.ToolName) ? string.Empty : $" [{evt.ToolName}]";
        return $"{prefix}{evt.Type}{tool}: {evt.Message}";
    }
}
