using System.Text.RegularExpressions;
using System.Text.Json;

namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private static void CapturePathHints(
        List<string> pathHints,
        string workspaceRoot,
        string toolName,
        JsonElement arguments,
        ToolResult result)
    {
        var path = ToolArgumentReader.GetString(arguments, "path");
        var allowMissingPath = toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                               toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
        TrackPathHint(pathHints, workspaceRoot, path, allowMissingPath);

        var pathspec = ToolArgumentReader.GetString(arguments, "pathspec");
        TrackPathHint(pathHints, workspaceRoot, pathspec, true);

        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Take(100))
            {
                if ((toolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
                     toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
                    TryExtractSearchHitPath(line, out var searchPath))
                {
                    TrackPathHint(pathHints, workspaceRoot, searchPath, false);
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

                        TrackPathHint(pathHints, workspaceRoot, candidate, false);
                    }
                }

                if (TryExtractGenericPathFromLine(line, out var genericPath))
                {
                    TrackPathHint(pathHints, workspaceRoot, genericPath, true);
                }
            }
        }
    }

    private static void TrackPathHint(List<string> pathHints, string workspaceRoot, string? rawPath, bool allowNonExisting)
    {
        if (!TryNormalizePathCandidate(workspaceRoot, rawPath, allowNonExisting, preferFile: false, out var normalized))
        {
            return;
        }

        if (pathHints.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        pathHints.Add(normalized);
        if (pathHints.Count > 64)
        {
            pathHints.RemoveAt(0);
        }
    }

    private static bool TryInferPathFromContext(
        string task,
        string reason,
        string workspaceRoot,
        IReadOnlyList<string> pathHints,
        bool allowNonExisting,
        bool preferFile,
        out string path)
    {
        foreach (var text in new[] { reason, task })
        {
            foreach (var candidate in ExtractPathCandidatesFromText(text))
            {
                if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
                {
                    return true;
                }
            }
        }

        for (var i = pathHints.Count - 1; i >= 0; i--)
        {
            if (TryNormalizePathCandidate(workspaceRoot, pathHints[i], allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (var text in new[] { reason, task })
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\b([A-Za-z0-9_\-]+\.[A-Za-z0-9_]{1,12})\b"))
            {
                var fileName = match.Groups[1].Value;
                if (TryFindUniqueFileByName(workspaceRoot, fileName, out path))
                {
                    return true;
                }
            }
        }

        path = string.Empty;
        return false;
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

    private static bool TryNormalizePathCandidate(
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

    private static bool TryFindUniqueFileByName(string workspaceRoot, string fileName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string? match = null;
        foreach (var file in Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            if (ShouldSkipPathScan(relative))
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

    private static bool ShouldSkipPathScan(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/');
        return rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith(".evoloop/", StringComparison.OrdinalIgnoreCase);
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
