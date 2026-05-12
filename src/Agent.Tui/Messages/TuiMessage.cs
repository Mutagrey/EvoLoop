namespace Agent.Tui;

internal enum TuiMessageRole
{
    System,
    User,
    Assistant,
    Error,
    Status
}

internal sealed record TuiMessage(
    TuiMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc)
{
    public static TuiMessage System(string content) => new(TuiMessageRole.System, content, DateTimeOffset.UtcNow);
    public static TuiMessage User(string content) => new(TuiMessageRole.User, content, DateTimeOffset.UtcNow);
    public static TuiMessage Error(string content) => new(TuiMessageRole.Error, content, DateTimeOffset.UtcNow);
    public static TuiMessage Status(string content) => new(TuiMessageRole.Status, content, DateTimeOffset.UtcNow);
}
