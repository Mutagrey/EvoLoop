using Agent.Hosting;
using Agent.Core;

namespace Agent.Tui;

internal sealed record TuiRuntimeInfo(
    string Workspace,
    string RequestedWorkspace,
    string ConfigPath,
    string DefaultConfigPath,
    string Profile,
    string ModelProvider,
    string ModelId,
    ToolCallingMode ToolCallingMode,
    IReadOnlyList<string> ModelProfiles,
    IReadOnlyList<string> ProfileFallbackOrder,
    string ModeLabel,
    string ModelStatus,
    ApprovalPolicyMode ApprovalMode,
    string ThemeName,
    string ApiBaseUrl,
    string OpenAiCompatiblePath,
    string CustomPath,
    string ApiKeyEnvVar,
    bool OfflineStrict,
    bool ApiAuthConfigured,
    bool CanRunAgentTasks,
    bool ModelConfigured,
    bool ModelReachable,
    bool WorkspaceWritable,
    bool GitAvailable,
    bool RipgrepAvailable,
    bool SqliteAvailable,
    int MaxSteps,
    int MaxInvalidModelResponses,
    int MaxConsecutiveFinalWithoutTools,
    int InvalidResponsesBeforeProfileSwitch,
    int FinalWithoutToolsBeforeProfileSwitch,
    int ToolTimeoutSeconds,
    int MaxOutputBytes,
    int ModelMinOutputTokens,
    int ModelMaxOutputTokens,
    double ModelMinTemperature,
    double ModelMaxTemperature,
    int HistoryMaxMessages,
    int HistoryMaxChars,
    int HistoryKeepTailMessages,
    int ObservationMaxChars,
    int ContextProjectDocMaxChars,
    int ContextFileExcerptMaxChars,
    int ContextObservationBudgetChars,
    int ContextHistorySummaryChars,
    bool MemoryEnabled,
    int MemoryMaxRuns,
    int MemoryContextMaxChars,
    IReadOnlyList<string> PromptPaths)
{
    public TuiRuntimeInfo(
        string Workspace,
        string RequestedWorkspace,
        string Profile,
        string ModeLabel,
        string ModelStatus,
        ApprovalPolicyMode ApprovalMode,
        string ThemeName,
        bool OfflineStrict,
        bool ApiAuthConfigured,
        bool CanRunAgentTasks)
        : this(
            Workspace,
            RequestedWorkspace,
            AgentConfigLoader.GetDefaultConfigPath(),
            AgentConfigLoader.GetDefaultConfigPath(),
            Profile,
            "custom",
            "fake",
            ToolCallingMode.JsonReActFallback,
            new[] { Profile },
            Array.Empty<string>(),
            ModeLabel,
            ModelStatus,
            ApprovalMode,
            ThemeName,
            "http://localhost:8000",
            "/v1/chat/completions",
            "/api/chat",
            "EVOLOOP_API_KEY",
            OfflineStrict,
            ApiAuthConfigured,
            CanRunAgentTasks,
            CanRunAgentTasks,
            CanRunAgentTasks,
            true,
            true,
            false,
            false,
            120,
            6,
            5,
            2,
            2,
            120,
            64 * 1024,
            256,
            4096,
            0.0,
            0.7,
            80,
            120000,
            18,
            6000,
            10000,
            8000,
            5000,
            7000,
            true,
            24,
            7000,
            Array.Empty<string>())
    {
    }

    public static TuiRuntimeInfo From(AgentRuntimeContext context, TuiArguments arguments, string themeName)
    {
        var profile = context.Config.Models.TryGetValue(arguments.Profile, out var modelProfile)
            ? modelProfile
            : context.Config.Models.Values.FirstOrDefault() ?? new ModelProfileConfig();
        var runtime = context.Config.Runtime;
        return new TuiRuntimeInfo(
            context.Workspace,
            context.RequestedWorkspace,
            context.ConfigPath,
            AgentConfigLoader.GetDefaultConfigPath(),
            arguments.Profile,
            profile.Provider,
            profile.Model,
            profile.ToolCallingMode,
            context.Config.Models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            runtime.ProfileFallbackOrder.ToArray(),
            context.Capabilities.ModeLabel,
            context.Capabilities.ModelStatus,
            context.Config.Safety.DefaultApprovalMode,
            themeName,
            context.Config.Api.BaseUrl,
            context.Config.Api.OpenAiCompatiblePath,
            context.Config.Api.CustomPath,
            context.Config.Api.ApiKeyEnvVar,
            context.Config.Safety.OfflineStrictMode,
            AgentStartup.HasApiAuthConfigured(context.Config),
            context.Capabilities.CanRunAgentTasks,
            context.Capabilities.ModelConfigured,
            context.Capabilities.ModelReachable,
            context.Capabilities.WorkspaceWritable,
            context.Capabilities.GitAvailable,
            context.Capabilities.RipgrepAvailable,
            context.Capabilities.SqliteAvailable,
            runtime.MaxSteps,
            runtime.MaxInvalidModelResponses,
            runtime.MaxConsecutiveFinalWithoutTools,
            runtime.InvalidResponsesBeforeProfileSwitch,
            runtime.FinalWithoutToolsBeforeProfileSwitch,
            runtime.ToolTimeoutSeconds,
            runtime.MaxOutputBytes,
            runtime.ModelMinOutputTokens,
            runtime.ModelMaxOutputTokens,
            runtime.ModelMinTemperature,
            runtime.ModelMaxTemperature,
            runtime.HistoryMaxMessages,
            runtime.HistoryMaxChars,
            runtime.HistoryKeepTailMessages,
            runtime.ObservationMaxChars,
            runtime.ContextProjectDocMaxChars,
            runtime.ContextFileExcerptMaxChars,
            runtime.ContextObservationBudgetChars,
            runtime.ContextHistorySummaryChars,
            runtime.MemoryEnabled,
            runtime.MemoryMaxRuns,
            runtime.MemoryContextMaxChars,
            BuildPromptPaths(context.Workspace));
    }

    private static IReadOnlyList<string> BuildPromptPaths(string workspaceRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(home))
        {
            paths.Add(Path.Combine(home, ".evoloop-agent", "SYSTEM.md"));
            paths.Add(Path.Combine(home, ".evoloop-agent", "APPEND_SYSTEM.md"));
        }

        paths.Add(Path.Combine(workspaceRoot, ".evoloop", "SYSTEM.md"));
        paths.Add(Path.Combine(workspaceRoot, ".evoloop", "APPEND_SYSTEM.md"));
        paths.Add(Path.Combine(workspaceRoot, ".evoloop", "prompts"));
        return paths;
    }
}
