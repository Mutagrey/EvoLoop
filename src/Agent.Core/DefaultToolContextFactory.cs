namespace Agent.Core;

public sealed class DefaultToolContextFactory : IToolContextFactory
{
    private readonly AgentConfig _config;
    private readonly ISearchService _searchService;
    private readonly RuntimeCapabilities _capabilities;
    private readonly IPatchService _patchService;
    private readonly IEventLog _eventLog;

    public DefaultToolContextFactory(AgentConfig config, ISearchService searchService, RuntimeCapabilities? capabilities = null)
        : this(config, searchService, NullPatchService.Instance, NullEventLog.Instance, capabilities)
    {
    }

    public DefaultToolContextFactory(
        AgentConfig config,
        ISearchService searchService,
        IPatchService patchService,
        IEventLog? eventLog = null,
        RuntimeCapabilities? capabilities = null)
    {
        _config = config;
        _searchService = searchService;
        _patchService = patchService;
        _eventLog = eventLog ?? NullEventLog.Instance;
        _capabilities = capabilities ?? RuntimeCapabilities.Default;
    }

    public ToolContext Create(string workspaceRoot, string sessionId, string profileName, AgentExecutionMode executionMode, ApprovalPolicyMode approvalMode)
    {
        return new ToolContext(
            workspaceRoot,
            sessionId,
            profileName,
            executionMode,
            approvalMode,
            _config,
            _searchService,
            _capabilities,
            _patchService,
            _eventLog);
    }
}
