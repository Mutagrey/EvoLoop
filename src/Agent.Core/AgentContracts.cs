using System.Text.Json;

namespace Agent.Core;

public interface IAgentLoop
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}

public interface ITool
{
    string Name { get; }
    ToolSchema Schema { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct);
}

public interface IModelClient
{
    Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct);
    ModelCapabilities Capabilities { get; }
}

public interface IModelClientRouter
{
    IModelClient GetClient(string profileName);
    string ResolveModelName(string profileName);
}

public interface IPolicyEngine
{
    PolicyDecision Evaluate(ToolCall call, ToolContext context);
}

public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct);
}

public interface IAgentRunObserver
{
    Task OnEventAsync(AgentRunEvent evt, CancellationToken ct);
}

public interface IEventStore
{
    Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct);
    Task AppendStepAsync(SessionStep step, CancellationToken ct);
    Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct);
}

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> LexicalAsync(SearchQuery query, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> RerankAsync(string task, IReadOnlyList<SearchHit> candidates, CancellationToken ct);
}

public interface IToolContextFactory
{
    ToolContext Create(string workspaceRoot, string sessionId, string profileName);
}

public sealed record ToolSchema(
    string Description,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyDictionary<string, string> FieldDescriptions);

public sealed record ToolCall(string Name, JsonElement Arguments, string Reason);

public sealed record ToolResult(
    bool Success,
    string Message,
    string? StdOut = null,
    string? StdErr = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ToolContext(
    string WorkspaceRoot,
    string SessionId,
    string ProfileName,
    AgentConfig Config,
    ISearchService SearchService,
    Func<string, string, CancellationToken, Task<IReadOnlyList<SearchHit>>>? RerankFn = null);

public sealed record ModelCapabilities(bool SupportsStreaming, bool SupportsEmbeddings);

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelTurnRequest(
    string ProfileName,
    string Model,
    string SystemPrompt,
    IReadOnlyList<ModelMessage> Messages,
    double Temperature,
    int MaxTokens,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ModelTurnResult(
    string Content,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    string? Raw = null);

public sealed record PolicyDecision(PolicyDecisionKind Kind, string Reason);

public enum PolicyDecisionKind
{
    Allow,
    RequireApproval,
    Deny
}

public sealed record ApprovalRequest(string ToolName, string Reason, string ArgumentsPreview);

public sealed record AgentRunRequest(
    string Task,
    string WorkspaceRoot,
    string ProfileName,
    int? MaxSteps,
    IAgentRunObserver? Observer = null);

public sealed record AgentRunResult(
    bool Success,
    string FinalMessage,
    int Steps,
    string SessionId,
    IReadOnlyList<SessionStep> StepTrace);

public sealed record AgentRunEvent(AgentRunEventType Type, string Message, int? Step = null, string? ToolName = null);

public enum AgentRunEventType
{
    SessionStarted,
    ModelCallStarted,
    ModelCallCompleted,
    ToolDecision,
    PolicyDenied,
    ApprovalRequired,
    ApprovalGranted,
    ApprovalRejected,
    ToolExecutionStarted,
    ToolExecutionCompleted,
    SessionCompleted,
    Error
}

public sealed record SessionInfo(string SessionId, DateTimeOffset StartedAtUtc);

public sealed record SessionStep(
    string SessionId,
    int StepNumber,
    string Action,
    string ToolName,
    string Reasoning,
    bool Success,
    string Output,
    DateTimeOffset TimestampUtc,
    long DurationMs,
    string? Error = null);

public sealed record SearchQuery(
    string WorkspaceRoot,
    string Query,
    int MaxResults,
    string? Glob = null,
    bool CaseSensitive = false,
    bool IncludeHidden = false);

public sealed record SearchHit(
    string FilePath,
    int Line,
    string Snippet,
    double LexicalScore,
    double SemanticScore,
    double FinalScore);

public sealed class NullObserver : IAgentRunObserver
{
    public static readonly NullObserver Instance = new();

    private NullObserver() { }

    public Task OnEventAsync(AgentRunEvent evt, CancellationToken ct) => Task.CompletedTask;
}

public sealed class AgentConfig
{
    public ApiConfig Api { get; init; } = new();
    public Dictionary<string, ModelProfileConfig> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reasoning"] = new() { Provider = "custom", Model = "deepseek", Temperature = 0.15, MaxTokens = 1800 },
        ["fast"] = new() { Provider = "custom", Model = "qwen", Temperature = 0.10, MaxTokens = 900 },
        ["fallback"] = new() { Provider = "custom", Model = "glm", Temperature = 0.20, MaxTokens = 1200 }
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
    public List<string> AllowedNetworkHosts { get; init; } = new();
    public List<string> DeniedShellPatterns { get; init; } = new()
    {
        "rm -rf /",
        "mkfs",
        "dd if=",
        "shutdown",
        "reboot",
        "curl",
        "wget",
        "scp",
        "ssh"
    };
}

public sealed class RuntimeConfig
{
    public int MaxSteps { get; init; } = 30;
    public int ToolTimeoutSeconds { get; init; } = 120;
    public int MaxOutputBytes { get; init; } = 64 * 1024;
    public int ModelMinOutputTokens { get; init; } = 256;
    public int ModelMaxOutputTokens { get; init; } = 4096;
    public double ModelMinTemperature { get; init; } = 0.0;
    public double ModelMaxTemperature { get; init; } = 0.7;
    public int LexicalSearchDefaultMaxResults { get; init; } = 20;
    public int RerankCandidateLimit { get; init; } = 12;
}

public sealed class UiConfig
{
    public bool UseColor { get; init; } = true;
    public bool CompactMode { get; init; } = false;
}
