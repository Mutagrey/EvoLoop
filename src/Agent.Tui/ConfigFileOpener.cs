using System.Diagnostics;
using System.Text;

namespace Agent.Tui;

internal interface IConfigFileOpener
{
    ConfigOpenResult Open(string path);
}

internal sealed record ConfigOpenResult(bool Success, string Message);

internal sealed class DefaultConfigFileOpener : IConfigFileOpener
{
    public ConfigOpenResult Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ConfigOpenResult(false, "Config path is empty.");
        }

        var editor = ResolveEditor();
        if (string.IsNullOrWhiteSpace(editor))
        {
            return new ConfigOpenResult(false, $"No editor configured. Set VISUAL or EDITOR, then open: {path}");
        }

        try
        {
            var (fileName, argsPrefix) = SplitCommand(editor);
            var arguments = string.IsNullOrWhiteSpace(argsPrefix)
                ? Quote(path)
                : argsPrefix + " " + Quote(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false
            });
            return new ConfigOpenResult(true, $"Opened config with {fileName}: {path}");
        }
        catch (Exception ex)
        {
            return new ConfigOpenResult(false, $"Failed to open config: {ex.Message}. Path: {path}");
        }
    }

    private static string ResolveEditor()
    {
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        if (!string.IsNullOrWhiteSpace(visual))
        {
            return visual;
        }

        var editor = Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(editor))
        {
            return editor;
        }

        return OperatingSystem.IsWindows() ? "notepad.exe" : string.Empty;
    }

    private static (string FileName, string ArgumentsPrefix) SplitCommand(string command)
    {
        var parts = SplitArgs(command).ToList();
        if (parts.Count == 0)
        {
            return (command, string.Empty);
        }

        return (parts[0], string.Join(" ", parts.Skip(1).Select(QuoteIfNeeded)));
    }

    private static IEnumerable<string> SplitArgs(string command)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string QuoteIfNeeded(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? Quote(value) : value;

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
