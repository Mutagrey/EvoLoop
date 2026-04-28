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
    ToolMetadata Metadata { get; }
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

public interface IWorkspaceMemoryStore
{
    Task<WorkspaceMemoryContext> LoadContextAsync(string workspaceRoot, string task, CancellationToken ct);
    Task SaveRunAsync(WorkspaceMemoryRecord record, CancellationToken ct);
}

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> LexicalAsync(SearchQuery query, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> RerankAsync(string task, IReadOnlyList<SearchHit> candidates, CancellationToken ct);
}

public interface IToolContextFactory
{
    ToolContext Create(string workspaceRoot, string sessionId, string profileName, AgentExecutionMode executionMode, ApprovalPolicyMode approvalMode);
}

public interface IContextBuilder
{
    Task<IReadOnlyList<ModelMessage>> BuildInitialMessagesAsync(
        AgentRunRequest request,
        ToolContext context,
        IWorkspaceMemoryStore memoryStore,
        CancellationToken ct);
}

public interface IPromptBuilder
{
    string BuildSystemPrompt(IReadOnlyCollection<ITool> tools, ToolContext context);
}

public interface IToolTurnExecutor
{
    Task<ToolTurnExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct);
}

public interface IEventLog
{
    Task AppendAsync(AgentEventRecord evt, CancellationToken ct);
}

public interface ICommandPolicy
{
    CommandPolicyDecision Evaluate(string command, ToolContext context, ToolMetadata metadata);
}

public interface IPatchService
{
    Task<ToolResult> WriteFileAsync(FileWriteRequest request, ToolContext context, CancellationToken ct);
    Task<ToolResult> ApplyPatchAsync(FilePatchRequest request, ToolContext context, CancellationToken ct);
    Task<ToolResult> DeleteAsync(FileDeleteRequest request, ToolContext context, CancellationToken ct);
    Task<ToolResult> UndoLastAsync(string workspaceRoot, CancellationToken ct);
}

public sealed record ToolSchema(
    string Description,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyDictionary<string, string> FieldDescriptions);

public sealed record ToolMetadata(
    ToolRiskLevel RiskLevel,
    ToolCategory Category,
    bool MutatesWorkspace,
    IReadOnlyList<string> RequiresCapabilities,
    bool IsFallbackOnly = false);

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
    AgentExecutionMode ExecutionMode,
    ApprovalPolicyMode ApprovalMode,
    AgentConfig Config,
    ISearchService SearchService,
    RuntimeCapabilities Capabilities,
    IPatchService PatchService,
    IEventLog EventLog,
    Func<string, string, CancellationToken, Task<IReadOnlyList<SearchHit>>>? RerankFn = null)
{
    public ToolContext(
        string WorkspaceRoot,
        string SessionId,
        string ProfileName,
        AgentConfig Config,
        ISearchService SearchService,
        RuntimeCapabilities Capabilities)
        : this(
            WorkspaceRoot,
            SessionId,
            ProfileName,
            AgentExecutionMode.Run,
            ApprovalPolicyMode.WorkspaceWrite,
            Config,
            SearchService,
            Capabilities,
            NullPatchService.Instance,
            NullEventLog.Instance)
    {
    }
}

public enum AgentExecutionMode
{
    Interactive,
    Run,
    Plan,
    Review
}

public enum ApprovalPolicyMode
{
    ReadOnly,
    WorkspaceWrite,
    AutoEdit,
    DangerFullAccess
}

public enum ToolRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum ToolCategory
{
    FileRead,
    FileWrite,
    Search,
    Git,
    Shell,
    Planning,
    Review,
    Status,
    Memory
}

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
    AgentExecutionMode ExecutionMode,
    ApprovalPolicyMode ApprovalMode,
    int? MaxSteps,
    IAgentRunObserver? Observer = null)
{
    public AgentRunRequest(
        string Task,
        string WorkspaceRoot,
        string ProfileName,
        int? MaxSteps,
        IAgentRunObserver? Observer = null)
        : this(Task, WorkspaceRoot, ProfileName, AgentExecutionMode.Run, ApprovalPolicyMode.WorkspaceWrite, MaxSteps, Observer)
    {
    }
}

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
    MemoryLoaded,
    ContextCompacted,
    ModelCallStarted,
    ModelCallCompleted,
    ModelProfileSwitched,
    ModelDecisionRecovered,
    ModelResponseInvalid,
    FinalRejectedRequiresTool,
    ToolDecision,
    PolicyDenied,
    ApprovalRequired,
    ApprovalGranted,
    ApprovalRejected,
    ToolExecutionStarted,
    ToolExecutionCompleted,
    MemoryUpdated,
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

public sealed record WorkspaceMemoryContext(string Content, int EntriesUsed)
{
    public static readonly WorkspaceMemoryContext Empty = new(string.Empty, 0);
}

public sealed record WorkspaceMemoryRecord(
    string WorkspaceRoot,
    string SessionId,
    string Task,
    bool Success,
    string FinalMessage,
    IReadOnlyList<SessionStep> Steps,
    DateTimeOffset CompletedAtUtc);

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

public sealed record AgentEventRecord(
    string SessionId,
    string EventType,
    DateTimeOffset TimestampUtc,
    string Message,
    string? ToolName = null,
    bool? Success = null,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record CommandPolicyDecision(
    PolicyDecisionKind Kind,
    string Reason,
    IReadOnlyList<string>? CommandSegments = null);

public sealed record FileWriteRequest(
    string Path,
    string Content,
    bool CreateIfMissing,
    string? ExpectedHash);

public sealed record FilePatchRequest(
    string Path,
    string? UnifiedDiff,
    string? Content,
    string? ExpectedHash);

public sealed record FileDeleteRequest(
    string Path,
    bool Recursive);

public sealed record ToolExecutionRequest(
    ITool Tool,
    ToolCall Call,
    ToolContext Context,
    int Step,
    string Action,
    string Reasoning,
    IPolicyEngine PolicyEngine,
    IApprovalService ApprovalService,
    IEventStore EventStore,
    IAgentRunObserver Observer);

public sealed record ToolTurnExecutionResult(
    bool Executed,
    bool Success,
    ToolResult? Result,
    SessionStep? Step,
    string? ObservationMessage = null);

public sealed class NullObserver : IAgentRunObserver
{
    public static readonly NullObserver Instance = new();

    private NullObserver() { }

    public Task OnEventAsync(AgentRunEvent evt, CancellationToken ct) => Task.CompletedTask;
}

public sealed class NullWorkspaceMemoryStore : IWorkspaceMemoryStore
{
    public static readonly NullWorkspaceMemoryStore Instance = new();

    private NullWorkspaceMemoryStore() { }

    public Task<WorkspaceMemoryContext> LoadContextAsync(string workspaceRoot, string task, CancellationToken ct)
        => Task.FromResult(WorkspaceMemoryContext.Empty);

    public Task SaveRunAsync(WorkspaceMemoryRecord record, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class NullEventStore : IEventStore
{
    public static readonly NullEventStore Instance = new();

    private NullEventStore() { }

    public Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
        => Task.FromResult(new SessionInfo(Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow));

    public Task AppendStepAsync(SessionStep step, CancellationToken ct)
        => Task.CompletedTask;

    public Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class NullEventLog : IEventLog
{
    public static readonly NullEventLog Instance = new();

    private NullEventLog() { }

    public Task AppendAsync(AgentEventRecord evt, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class NullPatchService : IPatchService
{
    public static readonly NullPatchService Instance = new();

    private NullPatchService() { }

    public Task<ToolResult> WriteFileAsync(FileWriteRequest request, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));

    public Task<ToolResult> ApplyPatchAsync(FilePatchRequest request, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));

    public Task<ToolResult> DeleteAsync(FileDeleteRequest request, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));

    public Task<ToolResult> UndoLastAsync(string workspaceRoot, CancellationToken ct)
        => Task.FromResult(new ToolResult(false, "Patch service is unavailable."));
}

public sealed class AgentConfig
{
    public ApiConfig Api { get; init; } = new();
    public Dictionary<string, ModelProfileConfig> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reasoning"] = new() { Provider = "custom", Model = "deepseek", Temperature = 0.12, MaxTokens = 2200 },
        ["fast"] = new() { Provider = "custom", Model = "qwen", Temperature = 0.05, MaxTokens = 1000 },
        ["fallback"] = new() { Provider = "custom", Model = "glm", Temperature = 0.18, MaxTokens = 1600 }
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
    public int MaxSteps { get; init; } = 30;
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
