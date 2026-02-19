namespace Agent.Tools;

public static class ToolPath
{
    public static string ResolveInWorkspace(string workspaceRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return Path.GetFullPath(workspaceRoot);
        }

        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(workspaceRoot, path));

        return fullPath;
    }
}
