using Agent.Core;

namespace Agent.Tools;

public static class ToolPath
{
    public static string ResolveInWorkspace(string workspaceRoot, string? path)
    {
        return PathSafety.ResolveInWorkspace(workspaceRoot, path);
    }
}
