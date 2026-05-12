using Agent.Core;
using Agent.Hosting;

namespace Agent.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AgentStartup.ApplyPrivacyDefaults();

        try
        {
            var command = CliArguments.Parse(args);
            var context = await AgentRuntimeContext.CreateAsync(
                new AgentRuntimeOptions(command.Workspace, command.ConfigPath, command.OfflineStrict),
                CancellationToken.None);
            var useColor = command.NoColor ? false : context.Config.Ui.UseColor;
            var renderer = new AnsiRenderer(useColor, context.Config.Ui.CompactMode);
            CliWarnings.Write(renderer, context);

            if (command.Mode == CliMode.Doctor)
            {
                renderer.WriteHeader("EvoLoop Doctor");
                renderer.WritePanel("Capabilities", context.Capabilities.ToDisplayText());
                return 0;
            }

            using var host = AgentExecutionHost.Create(context, new ConsoleApprovalService(renderer));

            if (command.Mode is CliMode.Run or CliMode.Plan or CliMode.Review)
            {
                return await RunSingleTurnAsync(host, context, renderer, command);
            }

            await CliSession.RunReplAsync(
                host.Loop,
                host.Tools,
                renderer,
                context.Config,
                context.Workspace,
                command.Profile,
                host.MemoryStore,
                context.Capabilities,
                host.PatchService);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunSingleTurnAsync(
        AgentExecutionHost host,
        AgentRuntimeContext context,
        AnsiRenderer renderer,
        CliArguments command)
    {
        var task = command.Mode == CliMode.Review
            ? CliSession.BuildReviewTask(command.Task)
            : command.Task;
        if (string.IsNullOrWhiteSpace(task))
        {
            renderer.WriteError("Missing task. Usage: agent-cli run|plan \"your task\" [--profile reasoning|fast|fallback]");
            return 2;
        }

        var result = await CliSession.RunTaskAsync(
            host.Loop,
            renderer,
            task,
            context.Workspace,
            command.Profile,
            context.Capabilities,
            command.Mode switch
            {
                CliMode.Plan => AgentExecutionMode.Plan,
                CliMode.Review => AgentExecutionMode.Review,
                _ => AgentExecutionMode.Run
            },
            context.Config.Safety.DefaultApprovalMode,
            host.PatchService);
        return result.Success ? 0 : 1;
    }
}
