using Agent.Core;

namespace Agent.Tools;

public static class ToolPath
{
    public static string ResolveInWorkspace(
        string workspaceRoot,
        string? path,
        bool requireExistingPath = true,
        bool allowProtectedPaths = true)
    {
        return PathSafety.ResolveInWorkspace(workspaceRoot, path, requireExistingPath, allowProtectedPaths);
    }
}
