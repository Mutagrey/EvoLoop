namespace Agent.Core;

public sealed class DefaultToolContextFactory : IToolContextFactory
{
    private readonly AgentConfig _config;
    private readonly ISearchService _searchService;

    public DefaultToolContextFactory(AgentConfig config, ISearchService searchService)
    {
        _config = config;
        _searchService = searchService;
    }

    public ToolContext Create(string workspaceRoot, string sessionId, string profileName)
    {
        return new ToolContext(workspaceRoot, sessionId, profileName, _config, _searchService);
    }
}
