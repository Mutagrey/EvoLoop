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
            TryNormalizePathCandidate(workspaceRoot, byKey, allowNonExisting, preferFile, out path))
        {
            return true;
        }

        var patchFile = Regex.Match(rawModelOutput, @"(?im)^\*\*\*\s+(?:Add|Update|Delete)\s+File:\s*(?<path>.+)$");
        if (patchFile.Success &&
            TryNormalizePathCandidate(workspaceRoot, patchFile.Groups["path"].Value, allowNonExisting, preferFile, out path))
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

            if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
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

            if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
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

            if (TryNormalizePathCandidate(workspaceRoot, candidate, allowNonExisting, preferFile, out path))
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

    private static bool TryExtractCommandFromRawOutput(string rawModelOutput, out string command)
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

    private static bool TryExtractCommitMessage(string rawModelOutput, string task, out string message)
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
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(rawModelOutput) || keys.Count == 0)
        {
            return false;
        }

        var keyExpr = string.Join("|", keys.Select(Regex.Escape));
        var linePattern = $@"(?im)^\s*(?:[-*]\s*)?(?:[""'`])?(?:{keyExpr})(?:[""'`])?\s*[:=]\s*(?<v>.+?)\s*$";
        var lineMatch = Regex.Match(rawModelOutput, linePattern);
        if (lineMatch.Success)
        {
            var extracted = lineMatch.Groups["v"].Value.Trim().Trim('"', '\'', '`');
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                value = extracted;
                return true;
            }
        }

        var jsonPattern = $"(?is)\\\"(?:{keyExpr})\\\"\\s*:\\s*\\\"(?<v>[^\\\"]{{1,5000}})\\\"";
        var jsonMatch = Regex.Match(rawModelOutput, jsonPattern);
        if (jsonMatch.Success)
        {
            var extracted = jsonMatch.Groups["v"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                value = extracted;
                return true;
            }
        }

        var singleQuotedPattern = $"(?is)'(?:{keyExpr})'\\s*:\\s*'(?<v>[^']{{1,5000}})'";
        var singleQuotedMatch = Regex.Match(rawModelOutput, singleQuotedPattern);
        if (singleQuotedMatch.Success)
        {
            var extracted = singleQuotedMatch.Groups["v"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                value = extracted;
                return true;
            }
        }

        return false;
    }

    private static List<(string Lang, string Body)> ExtractCodeFences(string text)
    {
        var result = new List<(string Lang, string Body)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(text, "```(?<lang>[^\\r\\n`]*)\\r?\\n(?<body>[\\s\\S]*?)```"))
        {
            var lang = (match.Groups["lang"].Value ?? string.Empty).Trim();
            var body = match.Groups["body"].Value ?? string.Empty;
            result.Add((lang, body));
        }

        return result;
    }

    private static bool IsShellLanguage(string lang)
    {
        var normalized = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "bash" or "sh" or "zsh" or "shell" or "console" or "cmd" or "bat" or "powershell" or "ps1";
    }

    private static bool IsDiffLanguage(string lang)
    {
        var normalized = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "diff" or "patch";
    }

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
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var lines = body
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                if (line.StartsWith("$ ", StringComparison.Ordinal))
                {
                    return line[2..].Trim();
                }

                if (line.StartsWith("PS> ", StringComparison.OrdinalIgnoreCase))
                {
                    return line[4..].Trim();
                }

                return line;
            })
            .ToList();

        return string.Join('\n', lines);
    }

}
