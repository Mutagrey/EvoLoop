namespace Agent.Core;

public sealed class DefaultToolContextFactory : IToolContextFactory
{
    private readonly AgentConfig _config;
    private readonly ISearchService _searchService;
    private readonly RuntimeCapabilities _capabilities;

    public DefaultToolContextFactory(AgentConfig config, ISearchService searchService, RuntimeCapabilities? capabilities = null)
    {
        _config = config;
        _searchService = searchService;
        _capabilities = capabilities ?? RuntimeCapabilities.Default;
    }

    public ToolContext Create(string workspaceRoot, string sessionId, string profileName)
    {
        return new ToolContext(workspaceRoot, sessionId, profileName, _config, _searchService, _capabilities);
    }
}
