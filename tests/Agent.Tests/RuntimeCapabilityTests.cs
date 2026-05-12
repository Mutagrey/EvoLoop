using Agent.Core;
using Agent.Hosting;
using System.Text.Json;
using static TestAssert;

internal static class RuntimeCapabilityTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("Config loader creates default config at custom path", TestConfigLoaderCreatesDefaultConfigAtCustomPath),
        ("Config loader reads custom settings", TestConfigLoaderReadsCustomSettings),
        ("Config loader creates minimal default config", TestConfigLoaderCreatesMinimalDefaultConfig),
        ("Runtime config uses explicit profile fallback order", TestRuntimeConfigUsesExplicitProfileFallbackOrder),
        ("Effective config applies offline strict override", TestEffectiveConfigAppliesOfflineStrictOverride),
        ("Capability probe selects local-only degraded mode without model", TestCapabilityProbeSelectsLocalOnlyModeWithoutModel),
        ("Capability probe keeps local-only degraded when offline strict has no model", TestCapabilityProbeKeepsLocalOnlyDegradedWithoutModel),
        ("Capability probe selects offline strict mode when model is ready", TestCapabilityProbeSelectsOfflineStrictModeWhenModelReady)
    };

static Task TestConfigLoaderCreatesDefaultConfigAtCustomPath()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-config-default-" + Guid.NewGuid().ToString("n"));
    var configPath = Path.Combine(temp, "nested", "config.json");

    try
    {
        var config = AgentConfigLoader.LoadOrCreate(configPath);

        Assert(File.Exists(configPath), "Expected missing custom config to be created.");
        Assert(config.Models.ContainsKey("reasoning"), "Expected default reasoning model profile.");
        Assert(config.Safety.DefaultApprovalMode == ApprovalPolicyMode.AutoEdit, "Expected default approval mode.");
        return Task.CompletedTask;
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static Task TestConfigLoaderCreatesMinimalDefaultConfig()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-config-minimal-" + Guid.NewGuid().ToString("n"));
    var configPath = Path.Combine(temp, "config.json");

    try
    {
        AgentConfigLoader.LoadOrCreate(configPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;
        Assert(root.TryGetProperty("api", out _), "Expected api section.");
        Assert(root.TryGetProperty("models", out var models), "Expected models section.");
        Assert(models.TryGetProperty("reasoning", out _), "Expected reasoning profile.");
        Assert(!models.TryGetProperty("fast", out _), "Expected no default fast profile.");
        Assert(!models.TryGetProperty("fallback", out _), "Expected no default fallback profile.");
        Assert(!root.TryGetProperty("runtime", out _), "Expected advanced runtime defaults to stay out of generated config.");
        return Task.CompletedTask;
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestRuntimeConfigUsesExplicitProfileFallbackOrder()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-config-fallback-order-" + Guid.NewGuid().ToString("n"));
    var configPath = Path.Combine(temp, "config.json");
    Directory.CreateDirectory(temp);

    try
    {
        await File.WriteAllTextAsync(configPath, """
{
  "models": {
    "reasoning": {
      "provider": "custom",
      "model": "deepseek"
    },
    "fallback": {
      "provider": "custom",
      "model": "glm"
    }
  },
  "runtime": {
    "profileFallbackOrder": [ "fallback" ]
  }
}
""");

        var config = AgentConfigLoader.LoadOrCreate(configPath);
        Assert(config.Runtime.ProfileFallbackOrder.SequenceEqual(new[] { "fallback" }), "Expected explicit profile fallback order.");
        Assert(config.Models.ContainsKey("fallback"), "Expected optional fallback profile to load.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static async Task TestConfigLoaderReadsCustomSettings()
{
    var temp = Path.Combine(Path.GetTempPath(), "agent-config-load-" + Guid.NewGuid().ToString("n"));
    var configPath = Path.Combine(temp, "config.json");
    Directory.CreateDirectory(temp);

    try
    {
        await File.WriteAllTextAsync(configPath, """
{
  "api": {
    "baseUrl": "https://gateway.company.local",
    "apiKeyEnvVar": "CUSTOM_AGENT_KEY"
  },
  "models": {
    "reasoning": {
      "provider": "openai",
      "model": "corp-reasoning",
      "toolCallingMode": "NativeNonStreamingTools"
    }
  },
  "safety": {
    "offlineStrictMode": true,
    "defaultApprovalMode": "ReadOnly",
    "allowedNetworkHosts": [ "gateway.company.local" ]
  },
  "runtime": {
    "maxSteps": 7,
    "memoryEnabled": false
  }
}
""");

        var config = AgentConfigLoader.LoadOrCreate(configPath);

        Assert(config.Api.BaseUrl == "https://gateway.company.local", "Expected custom API base URL.");
        Assert(config.Api.ApiKeyEnvVar == "CUSTOM_AGENT_KEY", "Expected custom API env var.");
        Assert(config.Models["reasoning"].Provider == "openai", "Expected custom model provider.");
        Assert(config.Models["reasoning"].ToolCallingMode == ToolCallingMode.NativeNonStreamingTools, "Expected enum config value.");
        Assert(config.Safety.OfflineStrictMode, "Expected offline strict from config.");
        Assert(config.Safety.DefaultApprovalMode == ApprovalPolicyMode.ReadOnly, "Expected custom approval mode.");
        Assert(config.Safety.AllowedNetworkHosts.Contains("gateway.company.local"), "Expected allowed host from config.");
        Assert(config.Runtime.MaxSteps == 7, "Expected custom runtime max steps.");
        Assert(!config.Runtime.MemoryEnabled, "Expected custom memory setting.");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static Task TestEffectiveConfigAppliesOfflineStrictOverride()
{
    var loaded = new AgentConfig
    {
        Api = new ApiConfig
        {
            BaseUrl = "https://gateway.company.local"
        },
        Safety = new SafetyConfig
        {
            OfflineStrictMode = false,
            AllowedNetworkHosts = new List<string> { "already.allowed.local" }
        }
    };

    var effective = AgentStartup.BuildEffectiveConfig(loaded, offlineStrict: true);

    Assert(effective.Safety.OfflineStrictMode, "Expected CLI offline-strict override.");
    Assert(effective.Safety.AllowedNetworkHosts.Contains("already.allowed.local"), "Expected existing allowed host to remain.");
    Assert(effective.Safety.AllowedNetworkHosts.Contains("gateway.company.local"), "Expected API host to be allowed in offline strict mode.");
    return Task.CompletedTask;
}

static Task TestCapabilityProbeSelectsLocalOnlyModeWithoutModel()
{
    var config = new AgentConfig();
    var mode = RuntimeCapabilityProbe.DetermineOperatingMode(config, modelReady: false);
    Assert(mode == RuntimeOperatingMode.LocalOnlyDegraded, "Expected local-only degraded mode when model is unavailable.");
    return Task.CompletedTask;
}

static Task TestCapabilityProbeKeepsLocalOnlyDegradedWithoutModel()
{
    var config = new AgentConfig
    {
        Safety = new SafetyConfig
        {
            OfflineStrictMode = true
        }
    };

    var mode = RuntimeCapabilityProbe.DetermineOperatingMode(config, modelReady: false);
    Assert(mode == RuntimeOperatingMode.LocalOnlyDegraded, "Expected missing model to force local-only degraded mode.");
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
