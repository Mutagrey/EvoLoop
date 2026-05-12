using System.Text.Json;

namespace Agent.Core;

public interface ITool
{
    string Name { get; }
    ToolSchema Schema { get; }
    ToolMetadata Metadata { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct);
}

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> LexicalAsync(SearchQuery query, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> RerankAsync(string task, IReadOnlyList<SearchHit> candidates, CancellationToken ct);
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
