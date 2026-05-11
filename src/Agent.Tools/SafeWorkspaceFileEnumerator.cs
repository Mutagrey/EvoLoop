namespace Agent.Tools;

internal static class SafeWorkspaceFileEnumerator
{
    private static readonly string[] SkippedDirectoryPrefixes =
    {
        ".git",
        ".evoloop/storage",
        ".tooling",
        "artifacts",
        "bin",
        "obj"
    };

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z",
        ".dll",
        ".dylib",
        ".exe",
        ".gif",
        ".gz",
        ".ico",
        ".jpg",
        ".jpeg",
        ".pdf",
        ".pdb",
        ".png",
        ".so",
        ".tar",
        ".zip"
    };

    public static IEnumerable<string> EnumerateFiles(string workspaceRoot, bool includeHidden, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                children = Array.Empty<string>();
            }

            foreach (var child in children)
            {
                var relative = NormalizeRelative(workspaceRoot, child);
                if (ShouldSkipDirectory(relative, child, includeHidden))
                {
                    continue;
                }

                pending.Push(child);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                if (ShouldSkipFile(workspaceRoot, file, includeHidden))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    public static bool ShouldSkipFile(string workspaceRoot, string filePath, bool includeHidden)
    {
        var relative = NormalizeRelative(workspaceRoot, filePath);
        var fileName = Path.GetFileName(filePath);
        return ShouldSkipDirectory(relative, filePath, includeHidden) ||
               (!includeHidden && fileName.StartsWith(".", StringComparison.Ordinal)) ||
               SkippedExtensions.Contains(Path.GetExtension(filePath));
    }

    private static bool ShouldSkipDirectory(string relativePath, string fullPath, bool includeHidden)
    {
        var normalized = relativePath.Trim('/');
        if (string.IsNullOrEmpty(normalized) || normalized == ".")
        {
            return false;
        }

        var name = Path.GetFileName(fullPath);
        if (!includeHidden && name.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        return SkippedDirectoryPrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelative(string workspaceRoot, string path)
        => Path.GetRelativePath(workspaceRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
