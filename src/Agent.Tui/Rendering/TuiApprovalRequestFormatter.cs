using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Tui;

internal static class TuiApprovalRequestFormatter
{
    public static string FormatForDialog(ApprovalRequest request, int maxPreviewChars = 2000)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tool: {request.ToolName}");
        sb.AppendLine($"Reason: {request.Reason}");

        if (TryParseObject(request.ArgumentsPreview, out var document))
        {
            using (document)
            {
                AppendStructuredPreview(sb, request.ToolName, document.RootElement, maxPreviewChars);
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Arguments:");
            sb.AppendLine(Clip(request.ArgumentsPreview, maxPreviewChars));
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendStructuredPreview(StringBuilder sb, string toolName, JsonElement root, int maxPreviewChars)
    {
        AppendProperty(sb, root, "path", "Path");
        AppendProperty(sb, root, "cwd", "Cwd");
        AppendProperty(sb, root, "cmd", "Command");
        AppendProperty(sb, root, "command", "Command");

        if (toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase) &&
            TryGetString(root, "unified_diff", out var diff))
        {
            sb.AppendLine();
            sb.AppendLine("Diff:");
            sb.AppendLine(Clip(diff, maxPreviewChars));
            return;
        }

        if ((toolName.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("fs_patch", StringComparison.OrdinalIgnoreCase)) &&
            TryGetString(root, "content", out var content))
        {
            sb.AppendLine();
            sb.AppendLine("Content preview:");
            sb.AppendLine(Clip(content, maxPreviewChars));
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Arguments:");
        sb.AppendLine(Clip(root.GetRawText(), maxPreviewChars));
    }

    private static void AppendProperty(StringBuilder sb, JsonElement root, string name, string label)
    {
        if (TryGetString(root, name, out var value))
        {
            sb.AppendLine($"{label}: {value}");
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.GetRawText();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryParseObject(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            document = null!;
            return false;
        }
    }

    private static string Clip(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}
