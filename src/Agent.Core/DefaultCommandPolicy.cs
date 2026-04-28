using System.Text.RegularExpressions;

namespace Agent.Core;

internal sealed class DefaultCommandPolicy : ICommandPolicy
{
    private static readonly Regex SegmentSplitRegex = new(@"(\|\||&&|;|\|)", RegexOptions.Compiled);

    private static readonly string[] InteractiveMarkers =
    {
        "bash",
        "sh",
        "zsh",
        "pwsh",
        "powershell",
        "cmd",
        "python -i",
        "dotnet script"
    };

    private static readonly string[] BlockedFragments =
    {
        "dotnet restore",
        "nuget ",
        "npm install",
        "pnpm install",
        "yarn install",
        "pip install",
        "git reset --hard",
        "git clean -fd",
        "git clean -fdx",
        "git push",
        "git pull",
        "git fetch",
        "curl ",
        "wget ",
        "invoke-webrequest",
        "iwr ",
        "irm ",
        "ssh ",
        "scp "
    };

    public CommandPolicyDecision Evaluate(string command, ToolContext context, ToolMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandPolicyDecision(PolicyDecisionKind.Deny, "Shell command is empty.");
        }

        var segments = SplitSegments(command);
        if (segments.Count == 0)
        {
            return new CommandPolicyDecision(PolicyDecisionKind.Deny, "Shell command has no executable segments.");
        }

        if (segments.Any(IsInteractive))
        {
            return new CommandPolicyDecision(PolicyDecisionKind.Deny, "Interactive shell commands are not allowed.", segments);
        }

        if (segments.Any(IsBlocked))
        {
            return new CommandPolicyDecision(PolicyDecisionKind.Deny, "Shell command matches the blocked command policy.", segments);
        }

        if (context.ExecutionMode == AgentExecutionMode.Plan)
        {
            return new CommandPolicyDecision(PolicyDecisionKind.Deny, "Plan mode does not allow shell execution.", segments);
        }

        if (context.ApprovalMode == ApprovalPolicyMode.ReadOnly)
        {
            return new CommandPolicyDecision(PolicyDecisionKind.Deny, "Read-only approval mode does not allow shell execution.", segments);
        }

        if (metadata.IsFallbackOnly)
        {
            return new CommandPolicyDecision(
                PolicyDecisionKind.RequireApproval,
                "Shell execution is fallback-only and requires approval unless a specialized tool is unavailable.",
                segments);
        }

        return new CommandPolicyDecision(PolicyDecisionKind.Allow, "Shell command allowed by command policy.", segments);
    }

    private static List<string> SplitSegments(string command)
    {
        return SegmentSplitRegex
            .Split(command)
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part) &&
                           part is not "&&" and not "||" and not ";" and not "|")
            .ToList();
    }

    private static bool IsInteractive(string segment)
    {
        var normalized = segment.Trim().ToLowerInvariant();
        if (normalized.StartsWith("cmd.exe", StringComparison.Ordinal) && !normalized.Contains("/c", StringComparison.Ordinal))
        {
            return true;
        }

        return InteractiveMarkers.Any(marker =>
            normalized.Equals(marker, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(marker + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlocked(string segment)
    {
        return BlockedFragments.Any(fragment =>
            segment.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
