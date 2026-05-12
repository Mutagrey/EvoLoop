namespace Agent.Cli;

internal enum CliMode
{
    Repl,
    Run,
    Plan,
    Review,
    Doctor
}

internal sealed class CliArguments
{
    public CliMode Mode { get; init; } = CliMode.Repl;
    public string? Task { get; init; }
    public string Profile { get; init; } = "reasoning";
    public string? Workspace { get; init; }
    public string? ConfigPath { get; init; }
    public bool NoColor { get; init; }
    public bool OfflineStrict { get; init; }

    public static CliArguments Parse(string[] args)
    {
        var mode = CliMode.Repl;
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
        else if (args.Length > 0 && args[0].Equals("plan", StringComparison.OrdinalIgnoreCase))
        {
            mode = CliMode.Plan;
            i = 1;
            if (i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                task = args[i];
                i++;
            }
        }
        else if (args.Length > 0 && args[0].Equals("review", StringComparison.OrdinalIgnoreCase))
        {
            mode = CliMode.Review;
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
        else if (args.Length > 0 &&
                 (args[0].Equals("repl", StringComparison.OrdinalIgnoreCase) ||
                  args[0].Equals("interactive", StringComparison.OrdinalIgnoreCase)))
        {
            mode = CliMode.Repl;
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
                case "--model" when i + 1 < args.Length:
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
                    if ((mode == CliMode.Run || mode == CliMode.Plan || mode == CliMode.Review) &&
                        task is null &&
                        !arg.StartsWith("--", StringComparison.Ordinal))
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
