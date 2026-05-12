namespace Agent.Core;

public sealed class AgentConfig
{
    public ApiConfig Api { get; init; } = new();
    public Dictionary<string, ModelProfileConfig> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reasoning"] = new() { Provider = "custom", Model = "deepseek", Temperature = 0.12, MaxTokens = 2200 }
    };

    public WorkspaceConfig Workspace { get; init; } = new();
    public SafetyConfig Safety { get; init; } = new();
    public RuntimeConfig Runtime { get; init; } = new();
    public UiConfig Ui { get; init; } = new();
}

public sealed class ApiConfig
{
    public string BaseUrl { get; init; } = "http://localhost:8000";
    public string OpenAiCompatiblePath { get; init; } = "/v1/chat/completions";
    public string CustomPath { get; init; } = "/api/chat";
    public bool PreferJsonResponseFormat { get; init; } = true;
    public bool ResponseFormatFallbackWithoutJson { get; init; } = true;
    public string SystemPromptMode { get; init; } = "user";
    public bool SystemPromptFallbackToUserMessage { get; init; } = true;
    public string ApiKey { get; init; } = string.Empty;
    public string ApiKeyEnvVar { get; init; } = "EVOLOOP_API_KEY";
    public int TimeoutSeconds { get; init; } = 120;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelProfileConfig
{
    public string Provider { get; init; } = "custom";
    public string Model { get; init; } = "qwen";
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 1200;
    public ToolCallingMode ToolCallingMode { get; init; } = ToolCallingMode.JsonReActFallback;
    public bool ProbeToolCalling { get; init; } = false;
}

public sealed class WorkspaceConfig
{
    public string DefaultRoot { get; init; } = Directory.GetCurrentDirectory();
    public List<string> IgnoreGlobs { get; init; } = new() { "bin/**", "obj/**", ".git/**" };
}

public sealed class SafetyConfig
{
    public bool RequireApprovalForWrites { get; init; } = true;
    public bool RequireApprovalForCommits { get; init; } = true;
    public bool RequireApprovalForRiskyShell { get; init; } = true;
    public bool DenyOutsideWorkspace { get; init; } = true;
    public bool OfflineStrictMode { get; init; } = false;
    public ApprovalPolicyMode DefaultApprovalMode { get; init; } = ApprovalPolicyMode.WorkspaceWrite;
    public List<string> AllowedNetworkHosts { get; init; } = new();
    public List<string> DeniedShellPatterns { get; init; } = new()
    {
        "rm -rf /",
        "rm -rf",
        "del /f /s /q",
        "rmdir /s /q",
        "mkfs",
        "dd if=",
        "shutdown",
        "reboot",
        "git reset --hard",
        "git clean -fd",
        "curl",
        "wget",
        "scp",
        "ssh"
    };
}

public sealed class RuntimeConfig
{
    public List<string> ProfileFallbackOrder { get; init; } = new();
    public int MaxSteps { get; init; } = 120;
    public int MaxInvalidModelResponses { get; init; } = 6;
    public int MaxConsecutiveFinalWithoutTools { get; init; } = 5;
    public int InvalidResponsesBeforeProfileSwitch { get; init; } = 2;
    public int FinalWithoutToolsBeforeProfileSwitch { get; init; } = 2;
    public int ToolTimeoutSeconds { get; init; } = 120;
    public int MaxOutputBytes { get; init; } = 64 * 1024;
    public int ModelMinOutputTokens { get; init; } = 256;
    public int ModelMaxOutputTokens { get; init; } = 4096;
    public double ModelMinTemperature { get; init; } = 0.0;
    public double ModelMaxTemperature { get; init; } = 0.7;
    public int LexicalSearchDefaultMaxResults { get; init; } = 20;
    public int RerankCandidateLimit { get; init; } = 12;
    public bool MemoryEnabled { get; init; } = true;
    public int MemoryMaxRuns { get; init; } = 24;
    public int MemoryContextMaxChars { get; init; } = 7000;
    public int HistoryMaxMessages { get; init; } = 80;
    public int HistoryMaxChars { get; init; } = 120000;
    public int HistoryKeepTailMessages { get; init; } = 18;
    public int ObservationMaxChars { get; init; } = 6000;
    public bool AdaptivePromptingEnabled { get; init; } = true;
    public int ContextProjectDocMaxChars { get; init; } = 10000;
    public int ContextFileExcerptMaxChars { get; init; } = 8000;
    public int ContextObservationBudgetChars { get; init; } = 5000;
    public int ContextHistorySummaryChars { get; init; } = 7000;
}

public sealed class UiConfig
{
    public bool UseColor { get; init; } = true;
    public bool CompactMode { get; init; } = true;
}
