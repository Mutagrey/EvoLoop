using Agent.Core;

namespace Agent.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CliStartup.ApplyPrivacyDefaults();

        try
        {
            var command = CliArguments.Parse(args);
            var context = await CliRuntimeContext.CreateAsync(command, CancellationToken.None);
            context.WriteStartupWarnings();

            if (command.Mode == CliMode.Doctor)
            {
                context.Renderer.WriteHeader("EvoLoop Doctor");
                context.Renderer.WritePanel("Capabilities", context.Capabilities.ToDisplayText());
                return 0;
            }

            if (command.Mode == CliMode.Tui)
            {
                TuiSession.RunPreparedPlaceholder(context);
                return 0;
            }

            using var host = AgentExecutionHost.Create(context);

            if (command.Mode is CliMode.Run or CliMode.Plan or CliMode.Review)
            {
                return await RunSingleTurnAsync(host, context);
            }

            await CliSession.RunReplAsync(
                host.Loop,
                host.Tools,
                context.Renderer,
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

    private static async Task<int> RunSingleTurnAsync(AgentExecutionHost host, CliRuntimeContext context)
    {
        var command = context.Command;
        var task = command.Mode == CliMode.Review
            ? CliSession.BuildReviewTask(command.Task)
            : command.Task;
        if (string.IsNullOrWhiteSpace(task))
        {
            context.Renderer.WriteError("Missing task. Usage: agent run|plan \"your task\" [--profile reasoning|fast|fallback]");
            return 2;
        }

        var result = await CliSession.RunTaskAsync(
            host.Loop,
            context.Renderer,
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
