namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private static bool TryRepairToolDecision(
        string toolName,
        AgentDecision decision,
        string task,
        string workspaceRoot,
        ReActPathHints pathHints,
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
            else if (pathHints.TryInferPathFromContext(task, decision.Reason, allowNonExistingPath, preferFilePath, out var inferredPath))
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
                updates["query"] = ReActRecoveryHelpers.BuildSeedSearchQuery(task);
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

        var merged = ReActRecoveryHelpers.MergeArguments(decision.Arguments, updates);
        repaired = decision with { Arguments = merged };
        repairNote = $"Auto-repaired arguments for '{toolName}': {string.Join(", ", updates.Keys)}.";
        return true;
    }
}
