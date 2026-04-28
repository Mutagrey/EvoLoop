using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Agent.Core;

public enum RuntimeOperatingMode
{
    Full,
    OfflineStrict,
    LocalOnlyDegraded
}

public sealed record RuntimeCapabilities(
    RuntimeOperatingMode OperatingMode,
    string Platform,
    string Shell,
    bool ShellAvailable,
    bool WorkspaceWritable,
    bool GitAvailable,
    bool RipgrepAvailable,
    bool SqliteAvailable,
    bool ModelConfigured,
    bool ModelReachable,
    bool AuthConfigured,
    string WorkspaceStatus,
    string ModelStatus)
{
    public static RuntimeCapabilities Default { get; } = new(
        RuntimeOperatingMode.Full,
        Environment.OSVersion.Platform.ToString(),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
        true,
        true,
        true,
        false,
        false,
        true,
        true,
        false,
        "workspace storage available",
        "model connectivity not probed");

    public bool CanRunAgentTasks => ModelConfigured && ModelReachable;

    public string ModeLabel => OperatingMode switch
    {
        RuntimeOperatingMode.Full => "full",
        RuntimeOperatingMode.OfflineStrict => "offline-strict",
        _ => "local-only degraded"
    };

    public string ToDisplayText()
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                $"Mode: {ModeLabel}",
                $"Platform: {Platform}",
                $"Shell: {Shell} ({FormatFlag(ShellAvailable)})",
                $"Workspace storage: {WorkspaceStatus}",
                $"Git: {FormatFlag(GitAvailable)}",
                $"rg: {FormatFlag(RipgrepAvailable)}",
                $"sqlite3: {FormatFlag(SqliteAvailable)}",
                $"Model configured: {FormatFlag(ModelConfigured)}",
                $"Gateway reachable: {FormatFlag(ModelReachable)}",
                $"Auth present: {FormatFlag(AuthConfigured)}",
                $"Model status: {ModelStatus}"
            });
    }

    private static string FormatFlag(bool value) => value ? "yes" : "no";
}

public static class RuntimeCapabilityProbe
{
    public static async Task<RuntimeCapabilities> ProbeAsync(AgentConfig config, string workspaceRoot, CancellationToken ct)
    {
        var shell = DetectShell();
        var shellAvailable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || File.Exists(shell);
        var gitAvailable = CommandExists("git");
        var ripgrepAvailable = CommandExists("rg");
        var sqliteAvailable = CommandExists("sqlite3");
        var workspaceWritable = TryProbeWorkspaceWritable(workspaceRoot, out var workspaceStatus);

        var authConfigured = HasApiAuthConfigured(config);
        var modelConfigured = TryResolveGatewayEndpoint(config, out var endpoint, out var modelStatus);
        var modelReachable = false;
        if (modelConfigured)
        {
            modelReachable = await CanReachAsync(endpoint!, ct);
            modelStatus = modelReachable
                ? authConfigured ? "gateway reachable" : "gateway reachable; auth header/env not present"
                : "gateway not reachable from current machine";
        }

        return new RuntimeCapabilities(
            DetermineOperatingMode(config, modelConfigured && modelReachable),
            RuntimeInformation.OSDescription,
            shell,
            shellAvailable,
            workspaceWritable,
            gitAvailable,
            ripgrepAvailable,
            sqliteAvailable,
            modelConfigured,
            modelReachable,
            authConfigured,
            workspaceStatus,
            modelStatus);
    }

    public static RuntimeOperatingMode DetermineOperatingMode(AgentConfig config, bool modelReady)
    {
        if (!modelReady)
        {
            return RuntimeOperatingMode.LocalOnlyDegraded;
        }

        return config.Safety.OfflineStrictMode
            ? RuntimeOperatingMode.OfflineStrict
            : RuntimeOperatingMode.Full;
    }

    private static string DetectShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "cmd.exe";
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHELL"))
            ? "/bin/sh"
            : Environment.GetEnvironmentVariable("SHELL")!;
    }

    private static bool TryProbeWorkspaceWritable(string workspaceRoot, out string status)
    {
        try
        {
            var directory = Path.Combine(workspaceRoot, ".evoloop", "storage");
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probePath, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probePath);
            status = "workspace storage available";
            return true;
        }
        catch (Exception ex)
        {
            status = $"workspace storage unavailable: {ex.Message}";
            return false;
        }
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(command);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(1000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveGatewayEndpoint(AgentConfig config, out Uri? endpoint, out string status)
    {
        endpoint = null;
        if (config.Models.Count == 0)
        {
            status = "no model profiles configured";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.Api.BaseUrl))
        {
            status = "api.baseUrl is empty";
            return false;
        }

        if (!Uri.TryCreate(config.Api.BaseUrl, UriKind.Absolute, out endpoint))
        {
            status = "api.baseUrl is not a valid absolute URI";
            return false;
        }

        status = "gateway configured";
        return true;
    }

    private static bool HasApiAuthConfigured(AgentConfig config)
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

    private static async Task<bool> CanReachAsync(Uri endpoint, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var port = endpoint.IsDefaultPort
                ? endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
                : endpoint.Port;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            await client.ConnectAsync(endpoint.Host, port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
