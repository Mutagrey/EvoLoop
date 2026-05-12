using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core;

internal sealed class ReActPathHints
{
    private readonly string _workspaceRoot;
    private readonly List<string> _pathHints = new();

    public ReActPathHints(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
    }

    public void Capture(string toolName, JsonElement arguments, ToolResult result)
    {
        var path = ToolArgumentReader.GetString(arguments, "path");
        var allowMissingPath = toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                               toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
        Track(path, allowMissingPath);

        var pathspec = ToolArgumentReader.GetString(arguments, "pathspec");
        Track(pathspec, true);

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            return;
        }

        var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(100))
        {
            if ((toolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
                 toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
                TryExtractSearchHitPath(line, out var searchPath))
            {
                Track(searchPath, false);
                continue;
            }

            if (toolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("[FILE] ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[DIR] ", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = line.StartsWith("[FILE] ", StringComparison.OrdinalIgnoreCase)
                        ? line["[FILE] ".Length..].Trim()
                        : line["[DIR] ".Length..].Trim();
                    var markerIndex = candidate.IndexOf(" (", StringComparison.Ordinal);
                    if (markerIndex > 0)
                    {
                        candidate = candidate[..markerIndex];
                    }

                    Track(candidate, false);
                }
            }

            if (TryExtractGenericPathFromLine(line, out var genericPath))
            {
                Track(genericPath, true);
            }
        }
    }

    public bool TryInferPathFromContext(
        string task,
        string reason,
        bool allowNonExisting,
        bool preferFile,
        out string path)
    {
        foreach (var text in new[] { reason, task })
        {
            foreach (var candidate in ExtractPathCandidatesFromText(text))
            {
                if (TryNormalizePathCandidate(_workspaceRoot, candidate, allowNonExisting, preferFile, out path))
                {
                    return true;
                }
            }
        }

        for (var i = _pathHints.Count - 1; i >= 0; i--)
        {
            if (TryNormalizePathCandidate(_workspaceRoot, _pathHints[i], allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (var text in new[] { reason, task })
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\b([A-Za-z0-9_\-]+\.[A-Za-z0-9_]{1,12})\b"))
            {
                var fileName = match.Groups[1].Value;
                if (TryFindUniqueFileByName(fileName, out path))
                {
                    return true;
                }
            }
        }

        path = string.Empty;
        return false;
    }

    internal static bool TryNormalizePathCandidate(
        string workspaceRoot,
        string? rawCandidate,
        bool allowNonExisting,
        bool preferFile,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return false;
        }

        var candidate = rawCandidate.Trim()
            .Trim('"', '\'', '`')
            .TrimEnd('.', ',', ';', ':', ')', ']', '}');

        if (candidate.Length == 0 || candidate.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var lineSuffixed = Regex.Match(candidate, @"^(?<path>.+\.[A-Za-z0-9_]{1,12}):\d+$");
        if (lineSuffixed.Success)
        {
            candidate = lineSuffixed.Groups["path"].Value;
        }

        if (candidate.Contains('\n') || candidate.Contains('\r'))
        {
            return false;
        }

        string absolute;
        if (allowNonExisting)
        {
            var root = Path.GetFullPath(workspaceRoot);
            absolute = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate));
            if (!PathSafety.IsWithinWorkspace(root, absolute))
            {
                return false;
            }

            if (preferFile &&
                (candidate.EndsWith("/", StringComparison.Ordinal) || candidate.EndsWith("\\", StringComparison.Ordinal)))
            {
                return false;
            }
        }
        else
        {
            try
            {
                absolute = PathSafety.ResolveInWorkspace(workspaceRoot, candidate);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(absolute) && !Directory.Exists(absolute))
            {
                return false;
            }

            if (preferFile && Directory.Exists(absolute))
            {
                return false;
            }
        }

        normalized = Path.GetRelativePath(workspaceRoot, absolute).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = ".";
        }

        if (preferFile && normalized == ".")
        {
            return false;
        }

        return true;
    }

    private void Track(string? rawPath, bool allowNonExisting)
    {
        if (!TryNormalizePathCandidate(_workspaceRoot, rawPath, allowNonExisting, preferFile: false, out var normalized))
        {
            return;
        }

        if (_pathHints.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _pathHints.Add(normalized);
        if (_pathHints.Count > 64)
        {
            _pathHints.RemoveAt(0);
        }
    }

    private static IEnumerable<string> ExtractPathCandidatesFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "[`\"'](?<path>[^`\"'\\r\\n]{1,260})[`\"']"))
        {
            var candidate = match.Groups["path"].Value;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (Match match in Regex.Matches(text, @"(?<![\w/\\])(?<path>[A-Za-z0-9_\-./\\]{2,260}\.[A-Za-z0-9_\-]{1,12})(?![\w/\\])"))
        {
            var candidate = match.Groups["path"].Value;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private bool TryFindUniqueFileByName(string fileName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string? match = null;
        foreach (var file in EnumeratePathHintFiles())
        {
            if (!Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = WorkspaceScanRules.NormalizeRelativePath(Path.GetRelativePath(_workspaceRoot, file));
            if (WorkspaceScanRules.ShouldSkipPath(relative, includeHidden: false))
            {
                continue;
            }

            if (match is not null)
            {
                return false;
            }

            match = relative;
        }

        if (match is null)
        {
            return false;
        }

        path = match;
        return true;
    }

    private IEnumerable<string> EnumeratePathHintFiles()
    {
        var pending = new Stack<string>();
        pending.Push(_workspaceRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                directories = Array.Empty<string>();
            }

            foreach (var child in directories)
            {
                var relative = WorkspaceScanRules.NormalizeRelativePath(Path.GetRelativePath(_workspaceRoot, child)) + "/";
                if (!WorkspaceScanRules.ShouldSkipPath(relative, includeHidden: false))
                {
                    pending.Push(child);
                }
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
                var relative = WorkspaceScanRules.NormalizeRelativePath(Path.GetRelativePath(_workspaceRoot, file));
                if (!WorkspaceScanRules.ShouldSkipPath(relative, includeHidden: false))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool TryExtractSearchHitPath(string line, out string path)
    {
        var match = Regex.Match(line, @"^(?<path>.+?):\d+\s");
        if (match.Success)
        {
            path = match.Groups["path"].Value.Trim();
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static bool TryExtractGenericPathFromLine(string line, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var statusLine = Regex.Match(trimmed, @"^(?:[ MADRCU\?]{1,3})\s+(?<path>.+)$");
        if (statusLine.Success)
        {
            var candidate = statusLine.Groups["path"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) &&
                (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('.')))
            {
                path = candidate;
                return true;
            }
        }

        var diffHeader = Regex.Match(trimmed, @"^diff --git a/(?<path>.+?) b/.+$");
        if (diffHeader.Success)
        {
            path = diffHeader.Groups["path"].Value.Trim();
            return true;
        }

        var patchHeader = Regex.Match(trimmed, @"^(?:\+\+\+|---)\s+[ab]/(?<path>.+)$");
        if (patchHeader.Success)
        {
            path = patchHeader.Groups["path"].Value.Trim();
            return true;
        }

        var quoted = Regex.Match(trimmed, "[`\"'](?<path>[^`\"'\\r\\n]{1,260})[`\"']");
        if (quoted.Success)
        {
            var candidate = quoted.Groups["path"].Value.Trim();
            if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('.'))
            {
                path = candidate;
                return true;
            }
        }

        var token = Regex.Match(trimmed, @"(?<![\w])(?<path>(?:\.{1,2}|[A-Za-z0-9_\-]+)(?:[/\\][A-Za-z0-9_.\-]+)+[/\\]?)(?![\w])");
        if (token.Success)
        {
            path = token.Groups["path"].Value.Trim();
            return true;
        }

        return false;
    }
}
