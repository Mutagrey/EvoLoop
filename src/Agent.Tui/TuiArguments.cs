namespace Agent.Tui;

internal sealed class TuiArguments
{
    public string Profile { get; init; } = "reasoning";
    public string? Workspace { get; init; }
    public string? ConfigPath { get; init; }
    public bool NoColor { get; init; }
    public bool OfflineStrict { get; init; }

    public static TuiArguments Parse(string[] args)
    {
        var profile = "reasoning";
        string? workspace = null;
        string? configPath = null;
        var noColor = false;
        var offlineStrict = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length:
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
            }
        }

        return new TuiArguments
        {
            Profile = profile,
            Workspace = workspace,
            ConfigPath = configPath,
            NoColor = noColor,
            OfflineStrict = offlineStrict
        };
    }
}
