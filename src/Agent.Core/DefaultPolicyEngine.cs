using System.Text.Json;

namespace Agent.Core;

public sealed class DefaultPolicyEngine : IPolicyEngine
{
    private readonly AgentConfig _config;
    private readonly HashSet<string> _allowedNetworkHosts;

    public DefaultPolicyEngine(AgentConfig config)
    {
        _config = config;
        _allowedNetworkHosts = BuildAllowedNetworkHosts(config);
    }

    public PolicyDecision Evaluate(ToolCall call, ToolContext context)
    {
        if (_config.Safety.DenyOutsideWorkspace)
        {
            var path = ExtractPath(call.Arguments);
            if (!string.IsNullOrWhiteSpace(path) && IsOutsideWorkspace(path, context.WorkspaceRoot))
            {
                return new PolicyDecision(PolicyDecisionKind.Deny, "Path is outside workspace root.");
            }
        }

        if (call.Name.StartsWith("fs_", StringComparison.OrdinalIgnoreCase) &&
            (call.Name.Contains("write", StringComparison.OrdinalIgnoreCase) ||
             call.Name.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
             call.Name.Contains("delete", StringComparison.OrdinalIgnoreCase)) &&
            _config.Safety.RequireApprovalForWrites)
        {
            return new PolicyDecision(PolicyDecisionKind.RequireApproval, "File mutation requires user approval.");
        }

        if (call.Name.StartsWith("git_", StringComparison.OrdinalIgnoreCase) &&
            (call.Name.Equals("git_add", StringComparison.OrdinalIgnoreCase) ||
             call.Name.Equals("git_commit", StringComparison.OrdinalIgnoreCase)) &&
            _config.Safety.RequireApprovalForCommits)
        {
            return new PolicyDecision(PolicyDecisionKind.RequireApproval, "Git staging/commit requires user approval.");
        }

        if (call.Name.Equals("exec_shell", StringComparison.OrdinalIgnoreCase))
        {
            var command = ExtractCommand(call.Arguments);
            if (string.IsNullOrWhiteSpace(command))
            {
                return new PolicyDecision(PolicyDecisionKind.Deny, "Shell command is empty.");
            }

            if (_config.Safety.OfflineStrictMode && IsNetworkShellCommand(command))
            {
                if (ReferencesAllowedHost(command, _allowedNetworkHosts))
                {
                    return new PolicyDecision(
                        PolicyDecisionKind.RequireApproval,
                        "Offline strict mode: network command allowed only for approved gateway hosts.");
                }

                return new PolicyDecision(
                    PolicyDecisionKind.Deny,
                    "Offline strict mode denies network shell commands except approved gateway hosts.");
            }

            if (_config.Safety.DeniedShellPatterns.Any(pattern =>
                    command.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return new PolicyDecision(PolicyDecisionKind.Deny, "Shell command matches denied pattern.");
            }

            if (_config.Safety.RequireApprovalForRiskyShell && IsRiskyShell(command))
            {
                return new PolicyDecision(PolicyDecisionKind.RequireApproval, "Risky shell command requires approval.");
            }
        }

        return new PolicyDecision(PolicyDecisionKind.Allow, "Allowed by default policy.");
    }

    private static string? ExtractPath(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("path", out var pathEl) &&
            pathEl.ValueKind == JsonValueKind.String)
        {
            return pathEl.GetString();
        }

        return null;
    }

    private static string? ExtractCommand(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("command", out var commandEl) &&
            commandEl.ValueKind == JsonValueKind.String)
        {
            return commandEl.GetString();
        }

        return null;
    }

    private static bool IsOutsideWorkspace(string rawPath, string workspace)
    {
        var resolvedPath = Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(workspace, rawPath));
        var resolvedWorkspace = Path.GetFullPath(workspace);
        return !resolvedPath.StartsWith(resolvedWorkspace, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRiskyShell(string command)
    {
        return command.Contains("&&", StringComparison.Ordinal) ||
               command.Contains("||", StringComparison.Ordinal) ||
               command.Contains("|", StringComparison.Ordinal) ||
               command.Contains(">", StringComparison.Ordinal) ||
               command.Contains("$(", StringComparison.Ordinal) ||
               command.Contains("`", StringComparison.Ordinal);
    }

    private static HashSet<string> BuildAllowedNetworkHosts(AgentConfig config)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Uri.TryCreate(config.Api.BaseUrl, UriKind.Absolute, out var baseUri) && !string.IsNullOrWhiteSpace(baseUri.Host))
        {
            hosts.Add(baseUri.Host);
        }

        foreach (var host in config.Safety.AllowedNetworkHosts)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.Add(host.Trim());
            }
        }

        return hosts;
    }

    private static bool ReferencesAllowedHost(string command, HashSet<string> allowedHosts)
    {
        if (allowedHosts.Count == 0)
        {
            return false;
        }

        foreach (var host in allowedHosts)
        {
            if (command.Contains(host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNetworkShellCommand(string command)
    {
        var normalized = command.Trim();

        var patterns = new[]
        {
            "curl ", "wget ", "ssh ", "scp ", "sftp ", "ftp ", "nc ", "ncat ", "telnet ",
            "ping ", "traceroute ", "nslookup ", "dig ", "invoke-webrequest", "iwr ", "irm ",
            "git push", "git pull", "git fetch", "git clone", "git remote add"
        };

        return patterns.Any(pattern => normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
