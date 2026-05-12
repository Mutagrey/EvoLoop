using Agent.Core;
using Agent.Tools;

namespace Agent.Hosting;

public static class AgentStartup
{
    public static void ApplyPrivacyDefaults()
    {
        SetIfMissing("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        SetIfMissing("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
        SetIfMissing("DOTNET_NOLOGO", "1");
    }

    public static async Task<string> ResolveWorkspaceRootAsync(string requestedWorkspace, CancellationToken ct)
    {
        var root = Path.GetFullPath(requestedWorkspace);
        if (!Directory.Exists(root))
        {
            return root;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                new[] { "rev-parse", "--show-toplevel" },
                root,
                ct,
                8 * 1024);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return root;
            }

            var line = result.StdOut
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return root;
            }

            var gitRoot = Path.GetFullPath(line.Trim());
            return Directory.Exists(gitRoot) ? gitRoot : root;
        }
        catch
        {
            return root;
        }
    }

    public static AgentConfig BuildEffectiveConfig(AgentConfig loadedConfig, bool offlineStrict)
    {
        if (!offlineStrict)
        {
            return loadedConfig;
        }

        var safety = loadedConfig.Safety;
        var allowedHosts = new List<string>(safety.AllowedNetworkHosts);
        if (Uri.TryCreate(loadedConfig.Api.BaseUrl, UriKind.Absolute, out var baseUri) &&
            !string.IsNullOrWhiteSpace(baseUri.Host) &&
            !allowedHosts.Contains(baseUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            allowedHosts.Add(baseUri.Host);
        }

        return new AgentConfig
        {
            Api = loadedConfig.Api,
            Models = loadedConfig.Models,
            Workspace = loadedConfig.Workspace,
            Runtime = loadedConfig.Runtime,
            Ui = loadedConfig.Ui,
            Safety = new SafetyConfig
            {
                RequireApprovalForWrites = safety.RequireApprovalForWrites,
                RequireApprovalForCommits = safety.RequireApprovalForCommits,
                RequireApprovalForRiskyShell = safety.RequireApprovalForRiskyShell,
                DenyOutsideWorkspace = safety.DenyOutsideWorkspace,
                OfflineStrictMode = true,
                DefaultApprovalMode = safety.DefaultApprovalMode,
                AllowedNetworkHosts = allowedHosts,
                DeniedShellPatterns = safety.DeniedShellPatterns
            }
        };
    }

    public static bool HasApiAuthConfigured(AgentConfig config)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.Api.ApiKeyEnvVar)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(config.Api.ApiKey))
        {
            return true;
        }

        if (config.Api.Headers.ContainsKey("Authorization"))
        {
            return true;
        }

        return config.Api.Headers.Keys.Any(k =>
            k.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Api-Key", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetIfMissing(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
