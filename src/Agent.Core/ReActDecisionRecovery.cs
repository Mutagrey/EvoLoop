using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private static bool TryRepairToolDecision(
        string toolName,
        AgentDecision decision,
        string task,
        string workspaceRoot,
        IReadOnlyList<string> pathHints,
        string rawModelOutput,
        out AgentDecision repaired,
        out string repairNote)
    {
        var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (toolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "path"))
        {
            if (TryExtractPathFromRawOutput(rawModelOutput, workspaceRoot, allowNonExisting: true, preferFile: false, out var listPath))
            {
                updates["path"] = listPath;
            }
            else
            {
                updates["path"] = ".";
            }
        }

        if ((toolName.Equals("fs_read", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase)) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "path"))
        {
            var allowNonExistingPath = toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                                       toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
            var preferFilePath = !toolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase);
            if (TryExtractPathFromRawOutput(rawModelOutput, workspaceRoot, allowNonExistingPath, preferFilePath, out var rawPath))
            {
                updates["path"] = rawPath;
            }
            else if (TryInferPathFromContext(task, decision.Reason, workspaceRoot, pathHints, allowNonExistingPath, preferFilePath, out var inferredPath))
            {
                updates["path"] = inferredPath;
            }
        }

        if (toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "content"))
        {
            if (TryExtractContentFromRawOutput(rawModelOutput, out var content))
            {
                updates["content"] = content;
            }
        }

        if (toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "unified_diff") &&
            !ToolArgumentReader.HasValue(decision.Arguments, "content"))
        {
            if (TryExtractUnifiedDiffFromRawOutput(rawModelOutput, out var diff))
            {
                updates["unified_diff"] = diff;
            }
            else if (TryExtractContentFromRawOutput(rawModelOutput, out var patchContent))
            {
                updates["content"] = patchContent;
            }
        }

        if (toolName.Equals("exec_shell", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "command"))
        {
            if (TryExtractCommandFromRawOutput(rawModelOutput, out var command))
            {
                updates["command"] = command;
            }
        }

        if (toolName.Equals("git_commit", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "message"))
        {
            if (TryExtractCommitMessage(rawModelOutput, task, out var commitMessage))
            {
                updates["message"] = commitMessage;
            }
        }

        if (toolName.Equals("git_show", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "ref"))
        {
            if (TryExtractGitRefFromRawOutput(rawModelOutput, out var gitRef))
            {
                updates["ref"] = gitRef;
            }
            else
            {
                updates["ref"] = "HEAD";
            }
        }

        if (toolName.Equals("git_add", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "pathspec"))
        {
            if (TryExtractPathFromRawOutput(rawModelOutput, workspaceRoot, allowNonExisting: true, preferFile: false, out var pathspec))
            {
                updates["pathspec"] = pathspec;
            }
            else
            {
                updates["pathspec"] = ".";
            }
        }

        if ((toolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "query"))
        {
            if (TryExtractSearchQueryFromRawOutput(rawModelOutput, out var query))
            {
                updates["query"] = query;
            }
            else
            {
                updates["query"] = BuildSeedSearchQuery(task);
            }
        }

        if (toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase) &&
            !ToolArgumentReader.HasValue(decision.Arguments, "task") &&
            !string.IsNullOrWhiteSpace(task))
        {
            updates["task"] = task;
        }

        if (updates.Count == 0)
        {
            repaired = decision;
            repairNote = string.Empty;
            return false;
        }

        var merged = MergeArguments(decision.Arguments, updates);
        repaired = decision with { Arguments = merged };
        repairNote = $"Auto-repaired arguments for '{toolName}': {string.Join(", ", updates.Keys)}.";
        return true;
    }

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

    private static JsonElement MergeArguments(JsonElement source, IReadOnlyDictionary<string, object?> updates)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                map[property.Name] = property.Value.Clone();
            }
        }

        foreach (var update in updates)
        {
            map[update.Key] = JsonSerializer.SerializeToElement(update.Value);
        }

        var json = JsonSerializer.Serialize(map);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

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
