using System.Text.Json;

namespace Agent.Core;

public static class ToolActivityMetadata
{
    public const string SummaryKey = "activity.summary";
    public const string KindKey = "activity.kind";
    public const string PathKey = "activity.path";
    public const string QueryKey = "activity.query";
    public const string CommandKey = "activity.command";
    public const string SuccessKey = "activity.success";

    public static IReadOnlyDictionary<string, string> Build(string toolName, JsonElement arguments, ToolResult result)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SuccessKey] = result.Success ? "true" : "false"
        };

        if (!result.Success)
        {
            data[KindKey] = "error";
            data[SummaryKey] = $"{toolName} failed: {ToOneLine(!string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr : result.Message, 180)}";
            return MergeResultMetadata(data, result.Metadata);
        }

        var path = ToolArgumentReader.GetString(arguments, "path");
        var query = ToolArgumentReader.GetString(arguments, "query");
        var command = ToolArgumentReader.GetString(arguments, "command");
        var gitRef = ToolArgumentReader.GetString(arguments, "ref");
        var pathspec = ToolArgumentReader.GetString(arguments, "pathspec");

        switch (toolName)
        {
            case "fs_list":
                AddPath(data, path ?? ".");
                data[KindKey] = "explore";
                data[SummaryKey] = $"Listed {data[PathKey]}";
                break;
            case "fs_read":
                AddPath(data, path ?? "<missing path>");
                data[KindKey] = "read";
                data[SummaryKey] = $"Read {data[PathKey]}";
                break;
            case "fs_write":
                AddPath(data, path ?? "<missing path>");
                data[KindKey] = "edit";
                data[SummaryKey] = $"Wrote {data[PathKey]}";
                break;
            case "fs_patch":
                AddPath(data, path ?? "<missing path>");
                data[KindKey] = "edit";
                data[SummaryKey] = $"Patched {data[PathKey]}";
                break;
            case "fs_delete":
                AddPath(data, path ?? "<missing path>");
                data[KindKey] = "edit";
                data[SummaryKey] = $"Deleted {data[PathKey]}";
                break;
            case "search_lexical":
            case "search_semantic":
                data[KindKey] = "search";
                data[QueryKey] = ToOneLine(query ?? "<query>", 80);
                data[SummaryKey] = toolName.Equals("search_semantic", StringComparison.OrdinalIgnoreCase)
                    ? $"Searched semantically \"{data[QueryKey]}\""
                    : $"Searched \"{data[QueryKey]}\"";
                break;
            case "exec_shell":
                data[KindKey] = "command";
                data[CommandKey] = ToOneLine(command ?? "<command>", 140);
                data[SummaryKey] = $"Ran {data[CommandKey]}";
                break;
            case "git_status":
                AddCommand(data, "git status --short --branch");
                break;
            case "git_diff":
                AddCommand(data, "git diff");
                break;
            case "git_log":
                AddCommand(data, "git log --oneline");
                break;
            case "git_show":
                AddCommand(data, $"git show --stat {gitRef ?? "HEAD"}");
                break;
            case "git_add":
                AddCommand(data, $"git add -- {pathspec ?? "."}");
                break;
            case "git_commit":
                AddCommand(data, "git commit -m <message>");
                break;
            default:
                data[KindKey] = "tool";
                data[SummaryKey] = $"{toolName} completed";
                break;
        }

        return MergeResultMetadata(data, result.Metadata);
    }

    private static void AddPath(Dictionary<string, string> data, string path)
        => data[PathKey] = path.Replace('\\', '/').Trim();

    private static void AddCommand(Dictionary<string, string> data, string command)
    {
        data[KindKey] = "command";
        data[CommandKey] = command;
        data[SummaryKey] = $"Ran {command}";
    }

    private static IReadOnlyDictionary<string, string> MergeResultMetadata(
        Dictionary<string, string> activity,
        IReadOnlyDictionary<string, string>? resultMetadata)
    {
        if (resultMetadata is null)
        {
            return activity;
        }

        foreach (var pair in resultMetadata)
        {
            activity.TryAdd(pair.Key, pair.Value);
        }

        return activity;
    }

    private static string ToOneLine(string? value, int maxLength)
    {
        var oneLine = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "...";
    }
}
