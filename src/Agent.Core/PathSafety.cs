namespace Agent.Core;

public static class PathSafety
{
    private static readonly string[] ProtectedRelativePathPrefixes =
    {
        ".git/config",
        ".git/hooks",
        ".ssh",
        ".aws",
        ".gnupg",
        ".env",
        ".env.local",
        ".env.production",
        ".env.development",
        ".npmrc",
        ".pypirc"
    };

    public static string ResolveInWorkspace(
        string workspaceRoot,
        string? path,
        bool requireExistingPath = true,
        bool allowProtectedPaths = true)
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

        var canonicalRoot = GetCanonicalCandidatePath(root) ?? root;
        var canonicalResolved = GetCanonicalCandidatePath(resolved) ?? resolved;
        if (!IsWithinWorkspace(canonicalRoot, canonicalResolved))
        {
            throw new InvalidOperationException($"Path escapes workspace through link traversal: '{path}'.");
        }

        if (!allowProtectedPaths && IsProtectedPath(root, resolved))
        {
            throw new InvalidOperationException($"Path is protected by workspace policy: '{path}'.");
        }

        if (requireExistingPath && !File.Exists(resolved) && !Directory.Exists(resolved))
        {
            throw new InvalidOperationException($"Path does not exist: '{path}'.");
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

    public static bool IsProtectedPath(string workspaceRoot, string candidatePath)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var candidate = Path.GetFullPath(candidatePath);
        if (!IsWithinWorkspace(root, candidate))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, candidate)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .TrimStart('/');

        return ProtectedRelativePathPrefixes.Any(prefix =>
            relative.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetCanonicalPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
            }

            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
            }

            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetCanonicalCandidatePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return GetCanonicalPath(fullPath);
            }

            var canonical = root;
            var current = root;
            var parts = fullPath[root.Length..]
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var next = Path.Combine(current, part);
                var canonicalNext = Path.Combine(canonical, part);

                if (File.Exists(next) || Directory.Exists(next))
                {
                    canonical = GetCanonicalPath(canonicalNext) ?? GetCanonicalPath(next) ?? canonicalNext;
                    current = next;
                    continue;
                }

                canonical = canonicalNext;
                current = next;
            }

            return Path.GetFullPath(canonical);
        }
        catch
        {
            return null;
        }
    }
}
