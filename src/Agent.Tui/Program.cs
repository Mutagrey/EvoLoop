using Agent.Hosting;

namespace Agent.Tui;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AgentStartup.ApplyPrivacyDefaults();

        try
        {
            var command = TuiArguments.Parse(args);
            var context = await AgentRuntimeContext.CreateAsync(
                new AgentRuntimeOptions(command.Workspace, command.ConfigPath, command.OfflineStrict),
                CancellationToken.None);

            var app = new TuiApp(
                TuiRuntimeInfo.From(context, command),
                SlashCommandRegistry.CreateDefault());
            new TerminalGuiTuiHost().Run(app);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
