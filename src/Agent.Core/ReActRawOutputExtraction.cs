using System.Text.RegularExpressions;

namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private static bool TryExtractPathFromRawOutput(
        string rawModelOutput,
        string workspaceRoot,
        bool allowNonExisting,
        bool preferFile,
        out string path)
    {
        path = string.Empty;

        if (TryExtractNamedScalarValue(rawModelOutput,
            new[] { "path", "file", "file_path", "filepath", "filename", "target_path", "relative_path", "pathspec" },
            out var byKey) &&
            ReActPathHints.TryNormalizePathCandidate(workspaceRoot, byKey, allowNonExisting, preferFile, out path))
        {
            return true;
        }

        var patchFile = Regex.Match(rawModelOutput, @"(?im)^\*\*\*\s+(?:Add|Update|Delete)\s+File:\s*(?<path>.+)$");
        if (patchFile.Success &&
            ReActPathHints.TryNormalizePathCandidate(workspaceRoot, patchFile.Groups["path"].Value, allowNonExisting, preferFile, out path))
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(rawModelOutput, @"(?<![\w/\\])(?<path>[A-Za-z0-9_\-./\\]{2,260}\.[A-Za-z0-9_\-]{1,16})(?::\d+)?(?![\w/\\])"))
        {
            var candidate = match.Groups["path"].Value;
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (ReActPathHints.TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (Match match in Regex.Matches(rawModelOutput, "[`\"'](?<path>[^`\"'\\r\\n]{1,260})[`\"']"))
        {
            var candidate = match.Groups["path"].Value.Trim();
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (ReActPathHints.TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        foreach (Match match in Regex.Matches(rawModelOutput, @"(?<![\w])(?<path>(?:\.{1,2}|[A-Za-z0-9_\-]+)(?:[/\\][A-Za-z0-9_.\-]+)+[/\\]?)(?![\w])"))
        {
            var candidate = match.Groups["path"].Value;
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (ReActPathHints.TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractSearchQueryFromRawOutput(string rawModelOutput, out string query)
    {
        query = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "query", "search", "pattern", "keyword", "term", "text" }, out var byKey))
        {
            query = ToOneLine(byKey, 180);
            return !string.IsNullOrWhiteSpace(query);
        }

        var quoted = Regex.Match(rawModelOutput, "(?im)\\bsearch(?:\\s+for)?\\s+[\"'`](?<q>[^\"'`\\r\\n]{2,220})[\"'`]");
        if (quoted.Success)
        {
            query = ToOneLine(quoted.Groups["q"].Value, 180);
            return true;
        }

        return false;
    }

    internal static bool TryExtractCommandFromRawOutput(string rawModelOutput, out string command)
    {
        command = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "command", "cmd", "shell", "script" }, out var byKey))
        {
            command = ClipToChars(byKey.Trim(), 1000);
            return !string.IsNullOrWhiteSpace(command);
        }

        foreach (var fence in ExtractCodeFences(rawModelOutput))
        {
            if (!IsShellLanguage(fence.Lang) && !LooksLikeCommandBlock(fence.Body))
            {
                continue;
            }

            var normalized = NormalizeShellCommandBlock(fence.Body);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                command = ClipToChars(normalized, 1000);
                return true;
            }
        }

        var promptLine = Regex.Match(rawModelOutput, @"(?im)^\s*\$\s+(?<cmd>.+)$");
        if (promptLine.Success)
        {
            command = ClipToChars(promptLine.Groups["cmd"].Value.Trim(), 1000);
            return true;
        }

        return false;
    }

    internal static bool TryExtractCommitMessage(string rawModelOutput, string task, out string message)
    {
        message = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "message", "msg", "commit_message" }, out var byKey))
        {
            message = ClipToChars(byKey.Trim(), 180);
            return !string.IsNullOrWhiteSpace(message);
        }

        var commitQuoted = Regex.Match(rawModelOutput, "(?im)\\bcommit(?:\\s+message)?\\s*[:=]\\s*[\"'`](?<m>[^\"'`\\r\\n]{3,180})[\"'`]");
        if (commitQuoted.Success)
        {
            message = commitQuoted.Groups["m"].Value.Trim();
            return true;
        }

        var taskQuoted = Regex.Match(task ?? string.Empty, "[\"'`](?<m>[^\"'`\\r\\n]{3,180})[\"'`]");
        if (taskQuoted.Success)
        {
            message = taskQuoted.Groups["m"].Value.Trim();
            return true;
        }

        return false;
    }

    private static bool TryExtractGitRefFromRawOutput(string rawModelOutput, out string gitRef)
    {
        gitRef = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "ref", "revision", "sha", "commit" }, out var byKey))
        {
            gitRef = byKey.Trim();
            return !string.IsNullOrWhiteSpace(gitRef);
        }

        var headRef = Regex.Match(rawModelOutput, @"\bHEAD(?:~\d+)?\b", RegexOptions.IgnoreCase);
        if (headRef.Success)
        {
            gitRef = headRef.Value.ToUpperInvariant();
            return true;
        }

        var hash = Regex.Match(rawModelOutput, @"\b[0-9a-fA-F]{7,40}\b");
        if (hash.Success)
        {
            gitRef = hash.Value;
            return true;
        }

        return false;
    }

    private static bool TryExtractContentFromRawOutput(string rawModelOutput, out string content)
    {
        content = string.Empty;
        if (TryExtractNamedScalarValue(rawModelOutput, new[] { "content", "body", "new_content", "text" }, out var byKey))
        {
            content = ClipToChars(byKey, 32000);
            return !string.IsNullOrWhiteSpace(content);
        }

        foreach (var fence in ExtractCodeFences(rawModelOutput))
        {
            if (IsShellLanguage(fence.Lang) || IsDiffLanguage(fence.Lang))
            {
                continue;
            }

            var candidate = fence.Body.Trim('\r', '\n');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                content = ClipToChars(candidate, 32000);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractUnifiedDiffFromRawOutput(string rawModelOutput, out string unifiedDiff)
    {
        unifiedDiff = string.Empty;
        foreach (var fence in ExtractCodeFences(rawModelOutput))
        {
            if (!IsDiffLanguage(fence.Lang))
            {
                continue;
            }

            var candidate = fence.Body.Trim('\r', '\n');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                unifiedDiff = ClipToChars(candidate, 64000);
                return true;
            }
        }

        var beginPatch = rawModelOutput.IndexOf("*** Begin Patch", StringComparison.Ordinal);
        if (beginPatch >= 0)
        {
            var endPatch = rawModelOutput.IndexOf("*** End Patch", beginPatch, StringComparison.Ordinal);
            if (endPatch > beginPatch)
            {
                unifiedDiff = ClipToChars(rawModelOutput[beginPatch..(endPatch + "*** End Patch".Length)], 64000);
                return true;
            }
        }

        var start = rawModelOutput.IndexOf("\n--- ", StringComparison.Ordinal);
        if (start >= 0)
        {
            var candidate = rawModelOutput[(start + 1)..].Trim();
            if (candidate.Contains("\n+++ ", StringComparison.Ordinal) && candidate.Contains("\n@@", StringComparison.Ordinal))
            {
                unifiedDiff = ClipToChars(candidate, 64000);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractNamedScalarValue(string rawModelOutput, IReadOnlyList<string> keys, out string value)
        => TextScalarExtraction.TryExtractByNamedKeys(rawModelOutput, keys, out value);

    private static List<(string Lang, string Body)> ExtractCodeFences(string text)
        => TextScalarExtraction.ExtractCodeFences(text);

    private static bool IsShellLanguage(string lang)
        => TextScalarExtraction.IsShellLanguage(lang);

    private static bool IsDiffLanguage(string lang)
        => TextScalarExtraction.IsDiffLanguage(lang);

    private static bool LooksLikeCommandBlock(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var trimmed = body.Trim();
        if (trimmed.Contains('\n'))
        {
            return trimmed.Split('\n').Any(line => line.Contains(' ') || line.Contains('/') || line.Contains('\\'));
        }

        return trimmed.Contains(' ') || trimmed.Contains('/') || trimmed.Contains('\\');
    }

    private static string NormalizeShellCommandBlock(string body)
        => TextScalarExtraction.NormalizeShellCommandBlock(body);

}
