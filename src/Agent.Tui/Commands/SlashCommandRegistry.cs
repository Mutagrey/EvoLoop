using System.Text;

namespace Agent.Tui;

internal sealed record SlashCommand(
    string Name,
    string Description,
    string Usage,
    Func<SlashCommandRegistry, TuiCommandResult> Execute);

internal sealed record TuiCommandResult(
    bool Handled,
    bool ExitRequested,
    bool IsError,
    string Message);

internal sealed class SlashCommandRegistry
{
    private readonly List<SlashCommand> _commands;

    private SlashCommandRegistry(IEnumerable<SlashCommand> commands)
    {
        _commands = commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<SlashCommand> Commands => _commands;

    public static SlashCommandRegistry CreateDefault()
    {
        return new SlashCommandRegistry(new[]
        {
            new SlashCommand(
                "/exit",
                "Exit the TUI.",
                "/exit",
                _ => new TuiCommandResult(true, true, false, "Exiting EvoLoop TUI.")),
            new SlashCommand(
                "/help",
                "Show available commands.",
                "/help",
                registry => new TuiCommandResult(true, false, false, registry.BuildHelpText())),
            AppHandled("/config", "Show grouped runtime config.", "/config [path|open|reload]"),
            AppHandled("/diff", "Navigate latest review diff.", "/diff [files|next|prev|number]"),
            AppHandled("/model", "Show active model and gateway state.", "/model"),
            AppHandled("/models", "List model profiles and fallback order.", "/models"),
            AppHandled("/plan", "Run read-only plan mode.", "/plan <task>"),
            AppHandled("/review", "Review current workspace changes.", "/review [focus]"),
            AppHandled("/skills", "List workspace skills.", "/skills"),
            AppHandled("/status", "Show last task status.", "/status"),
            AppHandled("/task", "Run a normal task explicitly.", "/task <text>")
        });
    }

    private static SlashCommand AppHandled(string name, string description, string usage)
        => new(name, description, usage, _ => new TuiCommandResult(false, false, true, $"Usage: {usage}"));

    public IReadOnlyList<SlashCommand> Filter(string prefix)
    {
        var normalized = string.IsNullOrWhiteSpace(prefix) ? "/" : prefix.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return _commands
            .Where(c => c.Name.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public TuiCommandResult Execute(string input)
    {
        var name = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new TuiCommandResult(false, false, true, "Empty command.");
        }

        var command = _commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return new TuiCommandResult(
                false,
                false,
                true,
                $"Unknown command: {name}. Type /help for available commands.");
        }

        return command.Execute(this);
    }

    public string BuildHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available commands:");
        foreach (var command in _commands)
        {
            sb.AppendLine($"  {command.Usage,-10} {command.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("Task input runs through the shared agent runtime.");
        sb.AppendLine("Use /model, /models, /skills, /config, /status, /plan <task>, /review [focus], /diff, or plain text for run mode.");
        sb.AppendLine("After /review, use /diff files, /diff next, /diff prev, or /diff <number>.");
        return sb.ToString().TrimEnd();
    }
}
