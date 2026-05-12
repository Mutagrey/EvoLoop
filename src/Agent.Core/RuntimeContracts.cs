namespace Agent.Core;

public interface IAgentLoop
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
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

public sealed record AgentRunEvent(
    AgentRunEventType Type,
    string Message,
    int? Step = null,
    string? ToolName = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

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
