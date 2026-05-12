namespace Agent.Core;

internal static class ContextText
{
    public static string Clip(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..Math.Max(0, maxChars - 14)] + "\n[truncated]";
    }

    public static string ClipLine(string value, int maxChars)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine[..Math.Max(0, maxChars - 3)] + "...";
    }
}
