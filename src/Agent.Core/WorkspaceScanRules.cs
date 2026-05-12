namespace Agent.Core;

public static class WorkspaceScanRules
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

    public static bool ShouldSkipDirectory(string relativePath, string directoryName, bool includeHidden)
    {
        var normalized = NormalizeRelativePath(relativePath).Trim('/');
        if (string.IsNullOrEmpty(normalized) || normalized == ".")
        {
            return false;
        }

        if (!includeHidden && directoryName.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        return SkippedDirectoryPrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldSkipFile(string relativePath, string fileName, bool includeHidden)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return ShouldSkipDirectory(normalized, fileName, includeHidden) ||
               (!includeHidden && fileName.StartsWith(".", StringComparison.Ordinal)) ||
               SkippedExtensions.Contains(Path.GetExtension(normalized));
    }

    public static bool ShouldSkipPath(string relativePath, bool includeHidden)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var name = Path.GetFileName(normalized.TrimEnd('/'));
        return normalized.EndsWith("/", StringComparison.Ordinal)
            ? ShouldSkipDirectory(normalized, name, includeHidden)
            : ShouldSkipFile(normalized, name, includeHidden);
    }

    public static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
