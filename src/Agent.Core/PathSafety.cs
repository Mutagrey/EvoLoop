namespace Agent.Core;

public static class PathSafety
{
    public static string ResolveInWorkspace(string workspaceRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException("Workspace root is empty.");
        }

        var root = Path.GetFullPath(workspaceRoot);
        var resolved = string.IsNullOrWhiteSpace(path) || path == "."
            ? root
            : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

        if (!IsWithinWorkspace(root, resolved))
        {
            throw new InvalidOperationException($"Path is outside workspace: '{path}'.");
        }

        return resolved;
    }

    public static bool IsWithinWorkspace(string workspaceRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var root = Path.GetFullPath(workspaceRoot);
        var candidate = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(root, candidate);

        if (string.IsNullOrEmpty(relative) || relative == ".")
        {
            return true;
        }

        if (Path.IsPathRooted(relative))
        {
            return false;
        }

        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
