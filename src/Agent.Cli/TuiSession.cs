namespace Agent.Cli;

internal static class TuiSession
{
    public static void RunPreparedPlaceholder(CliRuntimeContext context)
    {
        context.Renderer.WriteHeader("EvoLoop Agent TUI");
        context.Renderer.WritePanel(
            "TUI",
            "TUI target prepared; implementation pending.\n\n" +
            $"Workspace: {context.Workspace}\n" +
            $"Profile: {context.Command.Profile}\n" +
            $"Mode: {context.Capabilities.ModeLabel}\n\n" +
            "Available now:\n" +
            "- agent run \"task\"\n" +
            "- agent plan \"task\"\n" +
            "- agent review\n" +
            "- agent doctor\n" +
            "- agent repl");
    }
}
