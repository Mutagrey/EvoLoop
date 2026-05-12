namespace Agent.Core;

internal sealed class ReActProfileSelection
{
    private readonly AgentConfig _config;
    private readonly IModelClientRouter _modelRouter;
    private readonly IToolContextFactory _contextFactory;

    public ReActProfileSelection(
        AgentConfig config,
        IModelClientRouter modelRouter,
        IToolContextFactory contextFactory)
    {
        _config = config;
        _modelRouter = modelRouter;
        _contextFactory = contextFactory;
    }

    public ToolCallingMode ResolveToolCallingMode(string profileName)
    {
        if (_config.Models.TryGetValue(profileName, out var profile))
        {
            return profile.ToolCallingMode;
        }

        return ToolCallingMode.JsonReActFallback;
    }

    public double GetTemperature(string profileName)
    {
        var raw = _config.Models.TryGetValue(profileName, out var profile) ? profile.Temperature : 0.2;
        var min = _config.Runtime.ModelMinTemperature;
        var max = _config.Runtime.ModelMaxTemperature;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(raw, min, max);
    }

    public int GetMaxTokens(string profileName)
    {
        var raw = _config.Models.TryGetValue(profileName, out var profile) ? profile.MaxTokens : 1200;
        var min = _config.Runtime.ModelMinOutputTokens;
        var max = _config.Runtime.ModelMaxOutputTokens;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(raw, min, max);
    }

    public List<string> BuildProfilePlan(string requestedProfile)
    {
        var ordered = new List<string>();
        void AddIfExists(string profile)
        {
            if (_config.Models.ContainsKey(profile) &&
                !ordered.Contains(profile, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(profile);
            }
        }

        AddIfExists(requestedProfile);
        AddIfExists("reasoning");
        AddIfExists("fallback");
        AddIfExists("fast");

        foreach (var profile in _config.Models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            AddIfExists(profile);
        }

        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("No model profiles configured.");
        }

        return ordered;
    }

    public bool TrySwitchProfile(
        IReadOnlyList<string> profilePlan,
        ref int profileIndex,
        ref string currentProfileName,
        ref string modelName,
        string workspaceRoot,
        string sessionId,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode,
        ref ToolContext context)
    {
        if (profileIndex + 1 >= profilePlan.Count)
        {
            return false;
        }

        profileIndex++;
        currentProfileName = profilePlan[profileIndex];
        modelName = _modelRouter.ResolveModelName(currentProfileName);
        context = _contextFactory.Create(workspaceRoot, sessionId, currentProfileName, executionMode, approvalMode);
        return true;
    }

    public static int GetSwitchThreshold(int rawThreshold)
    {
        return rawThreshold <= 0 ? 1 : rawThreshold;
    }
}

public sealed partial class ReActAgentLoop
{
    private static bool TaskLikelyRequiresTools(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return false;
        }

        var normalized = task.ToLowerInvariant();
        var keywords = new[]
        {
            "create", "edit", "update", "modify", "delete", "write", "patch",
            "file", "folder", "project", "repository", "repo", "git", "commit",
            "run", "build", "test", "search", "scan", "analyze code", "refactor"
        };

        return keywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal));
    }
}
