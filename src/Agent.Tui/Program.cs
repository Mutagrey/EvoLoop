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

            WriteWarnings(context);
            WritePlaceholder(context, command);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void WriteWarnings(AgentRuntimeContext context)
    {
        if (!context.Workspace.Equals(context.RequestedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"warn: workspace resolved to git root: {context.Workspace}");
        }

        if (context.Config.Safety.OfflineStrictMode)
        {
            Console.WriteLine("warn: offline strict mode is on.");
        }

        if (!AgentStartup.HasApiAuthConfigured(context.Config))
        {
            Console.WriteLine($"warn: API auth is not configured. Set {context.Config.Api.ApiKeyEnvVar} or config auth.");
        }

        if (!context.Capabilities.CanRunAgentTasks)
        {
            Console.WriteLine($"warn: agent is running in '{context.Capabilities.ModeLabel}' mode: {context.Capabilities.ModelStatus}.");
        }
    }

    private static void WritePlaceholder(AgentRuntimeContext context, TuiArguments command)
    {
        Console.WriteLine("EvoLoop Agent TUI");
        Console.WriteLine("-----------------");
        Console.WriteLine("TUI target prepared; implementation pending.");
        Console.WriteLine();
        Console.WriteLine($"Workspace: {context.Workspace}");
        Console.WriteLine($"Profile: {command.Profile}");
        Console.WriteLine($"Mode: {context.Capabilities.ModeLabel}");
        Console.WriteLine();
        Console.WriteLine("Use Agent.Cli for explicit commands:");
        Console.WriteLine("- Agent.Cli doctor");
        Console.WriteLine("- Agent.Cli run \"task\"");
        Console.WriteLine("- Agent.Cli plan \"task\"");
        Console.WriteLine("- Agent.Cli review");
    }
}
