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
            AgentExecutionHost? host = null;
            void AttachRuntime(AgentRuntimeContext runtimeContext)
            {
                host?.Dispose();
                host = AgentExecutionHost.Create(runtimeContext, new TuiApprovalService(app, app.RequestApprovalAsync));
                app.AttachTaskRunner(new TuiTaskRunner(new AgentTaskRunner(host, runtimeContext)));
            }

            AttachRuntime(context);
            app.AttachConfigReload(async ct =>
            {
                var reloaded = await AgentRuntimeContext.CreateAsync(
                    new AgentRuntimeOptions(command.Workspace, command.ConfigPath, command.OfflineStrict),
                    ct);
                AttachRuntime(reloaded);
                return TuiRuntimeInfo.From(reloaded, command, theme.Name);
            });

            try
            {
                new TerminalGuiTuiHost(theme).Run(app);
            }
            finally
            {
                host?.Dispose();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
