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

            var theme = TuiTheme.Resolve(command.Theme, command.NoColor);
            var app = new TuiApp(
                TuiRuntimeInfo.From(context, command, theme.Name),
                SlashCommandRegistry.CreateDefault());
            using var host = AgentExecutionHost.Create(context, new TuiApprovalService(app, app.RequestApprovalAsync));
            app.AttachTaskRunner(new TuiTaskRunner(new AgentTaskRunner(host, context)));
            new TerminalGuiTuiHost(theme).Run(app);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
