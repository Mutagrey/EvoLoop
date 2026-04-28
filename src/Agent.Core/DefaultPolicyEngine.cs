namespace Agent.Core;

public sealed class DefaultPolicyEngine : IPolicyEngine
{
    private readonly AgentConfig _config;
    private readonly HashSet<string> _allowedNetworkHosts;
    private readonly IReadOnlyDictionary<string, ToolMetadata> _toolMetadata;
    private readonly ICommandPolicy _commandPolicy;

    public DefaultPolicyEngine(AgentConfig config, ICommandPolicy? commandPolicy = null)
        : this(Array.Empty<ITool>(), config, commandPolicy)
    {
    }

    public DefaultPolicyEngine(IEnumerable<ITool> tools, AgentConfig config, ICommandPolicy? commandPolicy = null)
    {
        _config = config;
        _allowedNetworkHosts = BuildAllowedNetworkHosts(config);
        _commandPolicy = commandPolicy ?? new DefaultCommandPolicy();
        _toolMetadata = tools.ToDictionary(t => t.Name, t => t.Metadata, StringComparer.OrdinalIgnoreCase);
    }

    public PolicyDecision Evaluate(ToolCall call, ToolContext context)
    {
        var metadata = ResolveMetadata(call.Name);

        if (_config.Safety.DenyOutsideWorkspace)
        {
            if (HasPathOutsideWorkspace(call, context.WorkspaceRoot))
            {
                return new PolicyDecision(PolicyDecisionKind.Deny, "Path is outside workspace root.");
            }
        }

        if ((context.ExecutionMode == AgentExecutionMode.Plan || context.ExecutionMode == AgentExecutionMode.Review) &&
            (metadata.MutatesWorkspace || metadata.Category == ToolCategory.Shell))
        {
            return new PolicyDecision(PolicyDecisionKind.Deny, $"{context.ExecutionMode} mode does not allow workspace mutations or shell execution.");
        }

        if (context.ApprovalMode == ApprovalPolicyMode.ReadOnly &&
            (metadata.MutatesWorkspace || metadata.Category == ToolCategory.Shell))
        {
            return new PolicyDecision(PolicyDecisionKind.Deny, "Read-only approval mode blocks workspace mutations and shell execution.");
        }

        if (metadata.MutatesWorkspace && HasProtectedMutationPath(call, context.WorkspaceRoot))
        {
            return new PolicyDecision(PolicyDecisionKind.Deny, "Target path is protected by workspace safety policy.");
        }

        if (metadata.Category == ToolCategory.Shell)
        {
            var command = ToolArgumentReader.GetString(call.Arguments, "command");
            if (string.IsNullOrWhiteSpace(command))
            {
                return new PolicyDecision(PolicyDecisionKind.Deny, "Shell command is empty.");
            }

            var commandDecision = _commandPolicy.Evaluate(command, context, metadata);
            if (commandDecision.Kind != PolicyDecisionKind.Allow)
            {
                return new PolicyDecision(commandDecision.Kind, commandDecision.Reason);
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

            if (context.ApprovalMode == ApprovalPolicyMode.WorkspaceWrite)
            {
                return new PolicyDecision(PolicyDecisionKind.RequireApproval, "WorkspaceWrite mode requires approval for shell commands.");
            }

            if (_config.Safety.RequireApprovalForRiskyShell && IsRiskyShell(command))
            {
                return new PolicyDecision(PolicyDecisionKind.RequireApproval, "Risky shell command requires approval.");
            }
        }

        if (metadata.MutatesWorkspace &&
            context.ApprovalMode == ApprovalPolicyMode.WorkspaceWrite &&
            _config.Safety.RequireApprovalForWrites)
        {
            return new PolicyDecision(PolicyDecisionKind.RequireApproval, "File mutation requires user approval.");
        }

        if (metadata.Category == ToolCategory.Git &&
            call.Name is "git_add" or "git_commit")
        {
            if (context.ApprovalMode == ApprovalPolicyMode.WorkspaceWrite || context.ApprovalMode == ApprovalPolicyMode.AutoEdit)
            {
                if (_config.Safety.RequireApprovalForCommits)
                {
                    return new PolicyDecision(PolicyDecisionKind.RequireApproval, "Git staging/commit requires user approval.");
                }
            }
        }

        return new PolicyDecision(PolicyDecisionKind.Allow, "Allowed by default policy.");
    }

    private ToolMetadata ResolveMetadata(string toolName)
    {
        if (_toolMetadata.TryGetValue(toolName, out var metadata))
        {
            return metadata;
        }

        if (toolName.StartsWith("fs_", StringComparison.OrdinalIgnoreCase))
        {
            var mutates = toolName is "fs_write" or "fs_patch" or "fs_delete";
            return new ToolMetadata(mutates ? ToolRiskLevel.High : ToolRiskLevel.Low, mutates ? ToolCategory.FileWrite : ToolCategory.FileRead, mutates, Array.Empty<string>());
        }

        if (toolName.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
        {
            var mutates = toolName is "git_add" or "git_commit";
            return new ToolMetadata(mutates ? ToolRiskLevel.High : ToolRiskLevel.Low, ToolCategory.Git, mutates, new[] { "git" });
        }

        if (toolName.Equals("exec_shell", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolMetadata(ToolRiskLevel.Critical, ToolCategory.Shell, false, new[] { "shell" }, IsFallbackOnly: true);
        }

        return new ToolMetadata(ToolRiskLevel.High, ToolCategory.Status, false, Array.Empty<string>());
    }

    private static bool HasPathOutsideWorkspace(ToolCall call, string workspaceRoot)
    {
        var paths = new List<string?>(2)
        {
            ToolArgumentReader.GetString(call.Arguments, "path")
        };

        if (call.Name.Equals("exec_shell", StringComparison.OrdinalIgnoreCase))
        {
            paths.Add(ToolArgumentReader.GetString(call.Arguments, "cwd"));
        }

        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var resolved = Path.GetFullPath(Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(workspaceRoot, rawPath));
            if (!PathSafety.IsWithinWorkspace(workspaceRoot, resolved))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasProtectedMutationPath(ToolCall call, string workspaceRoot)
    {
        var path = ToolArgumentReader.GetString(call.Arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var resolved = PathSafety.ResolveInWorkspace(workspaceRoot, path, requireExistingPath: false, allowProtectedPaths: false);
            return PathSafety.IsProtectedPath(workspaceRoot, resolved);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
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
