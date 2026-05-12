using Agent.Core;

namespace Agent.Tools;

internal static class SafeWorkspaceFileEnumerator
{
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
                if (WorkspaceScanRules.ShouldSkipDirectory(relative, Path.GetFileName(child), includeHidden))
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
        return WorkspaceScanRules.ShouldSkipFile(relative, Path.GetFileName(filePath), includeHidden);
    }

    private static string NormalizeRelative(string workspaceRoot, string path)
        => WorkspaceScanRules.NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path));
}
