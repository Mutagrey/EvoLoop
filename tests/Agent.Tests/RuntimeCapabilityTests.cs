using Agent.Core;
using Agent.Hosting;
using static TestAssert;

internal static class RuntimeCapabilityTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("Capability probe selects local-only degraded mode without model", TestCapabilityProbeSelectsLocalOnlyModeWithoutModel),
        ("Capability probe selects offline strict mode when model is ready", TestCapabilityProbeSelectsOfflineStrictModeWhenModelReady)
    };

static Task TestCapabilityProbeSelectsLocalOnlyModeWithoutModel()
{
    var config = new AgentConfig();
    var mode = RuntimeCapabilityProbe.DetermineOperatingMode(config, modelReady: false);
    Assert(mode == RuntimeOperatingMode.LocalOnlyDegraded, "Expected local-only degraded mode when model is unavailable.");
    return Task.CompletedTask;
}

static Task TestCapabilityProbeSelectsOfflineStrictModeWhenModelReady()
{
    var config = new AgentConfig
    {
        Safety = new SafetyConfig
        {
            OfflineStrictMode = true
        }
    };

    var mode = RuntimeCapabilityProbe.DetermineOperatingMode(config, modelReady: true);
    Assert(mode == RuntimeOperatingMode.OfflineStrict, "Expected offline-strict mode when model is ready.");
    return Task.CompletedTask;
}
}
