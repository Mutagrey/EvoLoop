using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Cli;

internal sealed class AnsiRenderer
{
    private const int MinFrameWidth = 64;
    private const int MaxFrameWidth = 120;
    private const int StatusTagWidth = 9;
    private readonly bool _useColor;
    private readonly bool _compactMode;

    public bool SupportsTransientOutput { get; }

    public AnsiRenderer(bool useColor, bool compactMode)
    {
        _useColor = useColor && SupportsAnsiColor();
        _compactMode = compactMode;
        SupportsTransientOutput = !Console.IsOutputRedirected;
    }

    public void WriteHeader(string title)
    {
        var width = GetFrameWidth();
        WriteRaw(Colorize(title, ConsoleColor.Cyan));
        WriteRaw(Colorize("Autonomous coding agent CLI", ConsoleColor.DarkGray));
        WriteRaw(Colorize(new string('-', width), ConsoleColor.DarkGray));
    }

    public void WritePanel(string title, string body)
    {
        var width = GetFrameWidth();
        var safeTitle = TruncateInline(title, Math.Max(8, width / 3));
        var separator = new string('-', Math.Max(8, width - safeTitle.Length - 1));

        WriteRaw(string.Empty);
        WriteRaw($"{Colorize(safeTitle.ToLowerInvariant(), ConsoleColor.Cyan)} {Colorize(separator, ConsoleColor.DarkGray)}");
        foreach (var line in WrapText(body, width))
        {
            WriteRaw(line);
        }
    }

    public void WriteStatus(string tag, string message, ConsoleColor color, int depth = 0, bool isLast = false)
    {
        depth = Math.Max(0, depth);
        var normalizedTag = NormalizeTag(tag);
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var prefixPlain = _compactMode
            ? $"{normalizedTag} "
            : $"{timestamp}  {normalizedTag} ";
        var prefixColored = _useColor
            ? _compactMode
                ? $"{Colorize(normalizedTag, color)} "
                : $"{Colorize(timestamp, ConsoleColor.DarkGray)}  {Colorize(normalizedTag, color)} "
            : prefixPlain;
        var treePrefix = BuildTreePrefix(depth, isLast);
        var treePrefixColored = _useColor && treePrefix.Length > 0
            ? Colorize(treePrefix, ConsoleColor.DarkGray)
            : treePrefix;
        var indent = new string(' ', prefixPlain.Length + treePrefix.Length);
        var maxMessageWidth = Math.Max(24, GetFrameWidth() - prefixPlain.Length - treePrefix.Length - 1);
        var lines = WrapText(message, maxMessageWidth).ToList();
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        WriteRaw(prefixColored + treePrefixColored + lines[0]);
        foreach (var line in lines.Skip(1))
        {
            WriteRaw(indent + line);
        }
    }

    public void WriteInfo(string message) => WriteStatus("INFO", message, ConsoleColor.Gray);
    public void WriteWarn(string message) => WriteStatus("WARN", message, ConsoleColor.Yellow);
    public void WriteError(string message) => WriteStatus("ERROR", message, ConsoleColor.Red);

    private void WriteRaw(string text)
    {
        Console.WriteLine(text);
    }

    private string Colorize(string text, ConsoleColor color)
    {
        if (!_useColor)
        {
            return text;
        }

        var code = color switch
        {
            ConsoleColor.Black => "30",
            ConsoleColor.DarkRed => "31",
            ConsoleColor.DarkGreen => "32",
            ConsoleColor.DarkYellow => "33",
            ConsoleColor.DarkBlue => "34",
            ConsoleColor.DarkMagenta => "35",
            ConsoleColor.DarkCyan => "36",
            ConsoleColor.Gray => "37",
            ConsoleColor.DarkGray => "90",
            ConsoleColor.Red => "91",
            ConsoleColor.Green => "92",
            ConsoleColor.Yellow => "93",
            ConsoleColor.Blue => "94",
            ConsoleColor.Magenta => "95",
            ConsoleColor.Cyan => "96",
            ConsoleColor.White => "97",
            _ => "0"
        };

        return $"\u001b[{code}m{text}\u001b[0m";
    }

    private static bool SupportsAnsiColor()
    {
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION")) ||
            string.Equals(Environment.GetEnvironmentVariable("ConEmuANSI"), "ON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("ANSICON"), "1", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERM")))
        {
            return true;
        }

        return TryEnableVirtualTerminalProcessing();
    }

    private static bool TryEnableVirtualTerminalProcessing()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            if ((mode & EnableVirtualTerminalProcessing) != 0)
            {
                return true;
            }

            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }

    private int GetFrameWidth()
    {
        var width = 100;
        try
        {
            width = Console.WindowWidth > 0 ? Console.WindowWidth : 100;
        }
        catch
        {
            // keep default width
        }

        return Math.Clamp(width, MinFrameWidth, MaxFrameWidth);
    }

    private static string NormalizeTag(string tag)
    {
        var clean = string.IsNullOrWhiteSpace(tag) ? "status" : tag.Trim().ToLowerInvariant();
        if (clean.Length > StatusTagWidth)
        {
            clean = clean[..StatusTagWidth];
        }

        return clean.PadRight(StatusTagWidth);
    }

    private static string BuildTreePrefix(int depth, bool isLast)
    {
        if (depth <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(depth * 3);
        for (var level = 1; level < depth; level++)
        {
            sb.Append("  ");
        }

        sb.Append(isLast ? "\\- " : "|- ");
        return sb.ToString();
    }

    private static string TruncateInline(string value, int width)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= width)
        {
            return oneLine;
        }

        if (width <= 3)
        {
            return oneLine[..width];
        }

        return oneLine[..(width - 3)] + "...";
    }

    private static IEnumerable<string> WrapText(string? text, int width)
    {
        if (width <= 0)
        {
            yield return string.Empty;
            yield break;
        }

        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rows = normalized.Split('\n');
        foreach (var row in rows)
        {
            var line = row.TrimEnd();
            if (line.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (line.Length > width)
            {
                var take = width;
                var breakAt = line.LastIndexOf(' ', Math.Min(width - 1, line.Length - 1), Math.Min(width, line.Length));
                if (breakAt > 0)
                {
                    take = breakAt;
                }

                yield return line[..take].TrimEnd();
                line = line[take..].TrimStart();
            }

            yield return line;
        }
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
