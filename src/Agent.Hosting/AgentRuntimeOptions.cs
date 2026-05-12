namespace Agent.Hosting;

public sealed record AgentRuntimeOptions(
    string? Workspace,
    string? ConfigPath,
    bool OfflineStrict);
