using Agent.Cli;
using static TestAssert;

internal static class CliTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("CLI parser defaults to REPL and preserves explicit modes", TestCliParserModes)
    };

static Task TestCliParserModes()
{
    var empty = CliArguments.Parse(Array.Empty<string>());
    Assert(empty.Mode == CliMode.Repl, "Expected bare CLI target to start REPL.");

    var repl = CliArguments.Parse(new[] { "repl", "--profile", "reasoning" });
    Assert(repl.Mode == CliMode.Repl, "Expected repl mode.");
    Assert(repl.Profile == "reasoning", "Expected profile option to be parsed.");

    var modelAlias = CliArguments.Parse(new[] { "run", "inspect", "--model", "fast" });
    Assert(modelAlias.Profile == "fast", "Expected --model to map to profile.");

    var run = CliArguments.Parse(new[] { "run", "inspect", "--offline-strict" });
    Assert(run.Mode == CliMode.Run, "Expected run mode.");
    Assert(run.Task == "inspect", "Expected run task to be parsed.");
    Assert(run.OfflineStrict, "Expected offline strict flag.");

    var leadingOptions = CliArguments.Parse(new[] { "--workspace", "/tmp/project", "doctor" });
    Assert(leadingOptions.Mode == CliMode.Doctor, "Expected mode after leading global options.");
    Assert(leadingOptions.Workspace == "/tmp/project", "Expected leading workspace option.");

    return Task.CompletedTask;
}
}
