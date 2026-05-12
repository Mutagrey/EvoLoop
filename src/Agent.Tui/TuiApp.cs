namespace Agent.Tui;

internal sealed class TuiApp
{
    private readonly List<TuiMessage> _messages = new();

    public TuiApp(TuiRuntimeInfo runtime, SlashCommandRegistry commands)
    {
        Runtime = runtime;
        Commands = commands;
        AddStartupMessages();
    }

    public TuiRuntimeInfo Runtime { get; }
    public SlashCommandRegistry Commands { get; }
    public IReadOnlyList<TuiMessage> Messages => _messages;
    public bool ExitRequested { get; private set; }

    public string Header =>
        $"EvoLoop Agent | profile: {Runtime.Profile} | mode: {Runtime.ModeLabel}";

    public string StatusLine =>
        $"idle | approval: {Runtime.ApprovalMode} | cwd: {Runtime.Workspace}";

    public string Transcript(int maxWidth = 100) => TranscriptRenderer.Render(_messages, maxWidth);

    public TuiCommandResult Submit(string input)
    {
        var text = input.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TuiCommandResult(false, false, false, string.Empty);
        }

        _messages.Add(TuiMessage.User(text));

        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            var result = Commands.Execute(text);
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                _messages.Add(result.IsError ? TuiMessage.Error(result.Message) : TuiMessage.System(result.Message));
            }

            ExitRequested = result.ExitRequested;
            return result;
        }

        const string pending = "Agent integration pending. Minimal TUI shell only; use Agent.Cli for run, plan, review, or repl.";
        _messages.Add(TuiMessage.System(pending));
        return new TuiCommandResult(true, false, false, pending);
    }

    private void AddStartupMessages()
    {
        _messages.Add(TuiMessage.System("Minimal TUI shell ready. Type /help for commands or /exit to quit."));
        _messages.Add(TuiMessage.Status($"Workspace: {Runtime.Workspace}"));
        _messages.Add(TuiMessage.Status($"Profile: {Runtime.Profile}; runtime mode: {Runtime.ModeLabel}"));

        if (!Runtime.Workspace.Equals(Runtime.RequestedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            _messages.Add(TuiMessage.Status($"Workspace resolved to git root: {Runtime.Workspace}"));
        }

        if (Runtime.OfflineStrict)
        {
            _messages.Add(TuiMessage.Status("Offline strict mode is on."));
        }

        if (!Runtime.ApiAuthConfigured)
        {
            _messages.Add(TuiMessage.Status("API auth is not configured."));
        }

        if (!Runtime.CanRunAgentTasks)
        {
            _messages.Add(TuiMessage.Status($"Model execution unavailable: {Runtime.ModelStatus}"));
        }
    }
}
