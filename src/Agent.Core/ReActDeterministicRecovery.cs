using System.Text.Json;

namespace Agent.Core;

internal sealed class ReActDeterministicRecovery
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    public ReActDeterministicRecovery(IReadOnlyDictionary<string, ITool> tools)
    {
        _tools = tools;
    }

    public bool TryBuildRecoveryDecision(
        string failedToolName,
        IReadOnlyList<string> missingRequired,
        AgentDecision currentDecision,
        string task,
        string workspaceRoot,
        ReActPathHints pathHints,
        out AgentDecision recovered,
        out string note)
    {
        recovered = currentDecision;
        note = string.Empty;
        if (missingRequired.Count == 0)
        {
            return false;
        }

        var missingSet = new HashSet<string>(missingRequired, StringComparer.OrdinalIgnoreCase);

        if ((failedToolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase) ||
             failedToolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)) &&
            missingSet.Contains("query"))
        {
            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["query"] = ReActRecoveryHelpers.BuildSeedSearchQuery(task)
            };
            if (failedToolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase) &&
                !ToolArgumentReader.HasValue(currentDecision.Arguments, "task"))
            {
                updates["task"] = task;
            }

            recovered = currentDecision with { Arguments = ReActRecoveryHelpers.MergeArguments(currentDecision.Arguments, updates) };
            note = $"Auto-filled missing query for '{failedToolName}' using deterministic task seed.";
            return true;
        }

        if (failedToolName.Equals("exec_shell", StringComparison.OrdinalIgnoreCase) &&
            missingSet.Contains("command") &&
            ReActAgentLoop.TryExtractCommandFromRawOutput(task, out var inferredCommand))
        {
            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = inferredCommand
            };
            recovered = currentDecision with { Arguments = ReActRecoveryHelpers.MergeArguments(currentDecision.Arguments, updates) };
            note = "Auto-filled missing shell command from task text.";
            return true;
        }

        if (failedToolName.Equals("git_commit", StringComparison.OrdinalIgnoreCase) &&
            missingSet.Contains("message"))
        {
            if (!ReActAgentLoop.TryExtractCommitMessage(task, task, out var commitMessage))
            {
                commitMessage = "chore: apply requested changes";
            }

            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["message"] = commitMessage
            };
            recovered = currentDecision with { Arguments = ReActRecoveryHelpers.MergeArguments(currentDecision.Arguments, updates) };
            note = "Auto-filled missing commit message.";
            return true;
        }

        if ((failedToolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
             failedToolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase)) &&
            missingSet.Contains("content") &&
            _tools.ContainsKey("fs_read"))
        {
            var path = ToolArgumentReader.GetString(currentDecision.Arguments, "path");
            if (ReActPathHints.TryNormalizePathCandidate(workspaceRoot, path, allowNonExisting: false, preferFile: true, out var normalizedPath))
            {
                recovered = ReActRecoveryHelpers.CreateToolDecision(
                    "fs_read",
                    "deterministic recovery: read existing file to prepare missing write content",
                    $"{{\"path\":{JsonSerializer.Serialize(normalizedPath)},\"max_bytes\":4096}}");
                note = $"Switched to 'fs_read' to recover missing content for '{failedToolName}'.";
                return true;
            }
        }

        if (missingSet.Contains("path"))
        {
            var allowNonExistingPath = failedToolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                                       failedToolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase);
            var preferFile = !failedToolName.Equals("fs_delete", StringComparison.OrdinalIgnoreCase);
            if (pathHints.TryInferPathFromContext(task, currentDecision.Reason, allowNonExistingPath, preferFile, out var inferredPath))
            {
                var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = inferredPath
                };
                recovered = currentDecision with { Arguments = ReActRecoveryHelpers.MergeArguments(currentDecision.Arguments, updates) };
                note = $"Recovered missing path for '{failedToolName}' from workspace/task hints.";
                return true;
            }

            if (_tools.ContainsKey("fs_list") && !failedToolName.Equals("fs_list", StringComparison.OrdinalIgnoreCase))
            {
                recovered = ReActRecoveryHelpers.CreateToolDecision(
                    "fs_list",
                    $"deterministic recovery: collect valid paths because '{failedToolName}' was missing path",
                    "{\"path\":\".\",\"recurse\":false,\"include_hidden\":false}");
                note = $"Switched to 'fs_list' because '{failedToolName}' was missing path.";
                return true;
            }

            if (_tools.ContainsKey("search_lexical") && !failedToolName.Equals("search_lexical", StringComparison.OrdinalIgnoreCase))
            {
                var seedQuery = ReActRecoveryHelpers.BuildSeedSearchQuery(task);
                recovered = ReActRecoveryHelpers.CreateToolDecision(
                    "search_lexical",
                    $"deterministic recovery: locate candidate files before retrying '{failedToolName}'",
                    $"{{\"query\":{JsonSerializer.Serialize(seedQuery)},\"max_results\":12}}");
                note = $"Switched to 'search_lexical' because '{failedToolName}' was missing path.";
                return true;
            }
        }

        return false;
    }

    public AgentDecision? TryCreateBootstrapDecision(
        bool requiresToolBeforeFinal,
        int toolStepsExecuted,
        int consecutiveInvalidResponses,
        string task)
    {
        if (!requiresToolBeforeFinal || toolStepsExecuted > 0 || consecutiveInvalidResponses < 1)
        {
            return null;
        }

        if (_tools.ContainsKey("fs_list"))
        {
            return ReActRecoveryHelpers.CreateToolDecision(
                "fs_list",
                "deterministic bootstrap: inspect workspace root before further decisions",
                "{\"path\":\".\",\"recurse\":false,\"include_hidden\":false}");
        }

        if (_tools.ContainsKey("git_status"))
        {
            return ReActRecoveryHelpers.CreateToolDecision(
                "git_status",
                "deterministic bootstrap: inspect repository state before further decisions",
                "{}");
        }

        if (_tools.ContainsKey("search_lexical"))
        {
            var seedQuery = ReActRecoveryHelpers.BuildSeedSearchQuery(task);
            return ReActRecoveryHelpers.CreateToolDecision(
                "search_lexical",
                "deterministic bootstrap: locate candidate code before further decisions",
                $"{{\"query\":{JsonSerializer.Serialize(seedQuery)},\"max_results\":12}}");
        }

        return null;
    }
}
