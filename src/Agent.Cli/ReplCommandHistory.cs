using System.Text;

namespace Agent.Cli;

internal sealed class ReplCommandHistory
{
    private readonly string? _path;
    private readonly int _maxEntries;
    private readonly List<string> _entries;

    private ReplCommandHistory(string? path, int maxEntries, List<string> entries)
    {
        _path = path;
        _maxEntries = Math.Max(50, maxEntries);
        _entries = entries;
    }

    public static async Task<ReplCommandHistory> OpenAsync(string workspaceRoot, int maxEntries, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, ".evoloop", "storage", "repl-commands.txt");
        var entries = new List<string>();
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(path))
            {
                var lines = await File.ReadAllLinesAsync(path, ct);
                foreach (var line in lines)
                {
                    var normalized = line.Trim();
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        entries.Add(normalized);
                    }
                }
            }
        }
        catch
        {
            return new ReplCommandHistory(null, maxEntries, entries);
        }

        if (entries.Count > maxEntries)
        {
            entries = entries.Skip(entries.Count - maxEntries).ToList();
        }

        return new ReplCommandHistory(path, maxEntries, entries);
    }

    public bool TryResolve(string token, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        var indexText = token[1..].Trim();
        if (!int.TryParse(indexText, out var index))
        {
            return false;
        }

        if (index < 1 || index > _entries.Count)
        {
            return false;
        }

        command = _entries[index - 1];
        return true;
    }

    public async Task AddAsync(string command, CancellationToken ct)
    {
        var normalized = command.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (_entries.Count > 0 && _entries[^1].Equals(normalized, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(normalized);
        if (_entries.Count > _maxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - _maxEntries);
        }

        if (!string.IsNullOrWhiteSpace(_path))
        {
            await File.WriteAllLinesAsync(_path, _entries, Encoding.UTF8, ct);
        }
    }

    public string FormatRecent(int take)
    {
        if (_entries.Count == 0)
        {
            return "No saved commands yet.";
        }

        var count = Math.Max(1, take);
        var start = Math.Max(0, _entries.Count - count);
        var sb = new StringBuilder();
        for (var i = start; i < _entries.Count; i++)
        {
            sb.AppendLine($"{i + 1,4}: {_entries[i]}");
        }

        sb.Append("Use !N to rerun by index.");
        return sb.ToString();
    }
}

