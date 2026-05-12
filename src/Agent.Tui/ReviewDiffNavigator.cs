using System.Text;

namespace Agent.Tui;

internal sealed class ReviewDiffNavigator
{
    private readonly List<ReviewDiffFile> _files;
    private int _index;

    private ReviewDiffNavigator(IEnumerable<ReviewDiffFile> files)
    {
        _files = files.ToList();
    }

    public int Count => _files.Count;
    public bool HasFiles => _files.Count > 0;

    public static ReviewDiffNavigator FromReviewSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new ReviewDiffNavigator(Array.Empty<ReviewDiffFile>());
        }

        var files = ParseGitDiff(summary);
        if (files.Count == 0)
        {
            files = ParseSnapshotDiff(summary);
        }

        return new ReviewDiffNavigator(files);
    }

    public string RenderCurrent(int maxChars = 6000)
    {
        if (_files.Count == 0)
        {
            return "No review diff is available yet. Run /review first.";
        }

        var file = _files[_index];
        var sb = new StringBuilder();
        sb.AppendLine($"Diff {_index + 1}/{_files.Count}: {file.Path}");
        sb.AppendLine($"Hunks: {file.HunkCount}");
        sb.AppendLine();
        sb.Append(Clip(file.Content, maxChars));
        return sb.ToString().TrimEnd();
    }

    public string RenderFiles()
    {
        if (_files.Count == 0)
        {
            return "No review diff is available yet. Run /review first.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Review diff files:");
        for (var i = 0; i < _files.Count; i++)
        {
            var marker = i == _index ? "*" : " ";
            sb.AppendLine($"{marker} {i + 1}. {_files[i].Path} ({_files[i].HunkCount} hunks)");
        }

        return sb.ToString().TrimEnd();
    }

    public string Next()
    {
        if (_files.Count == 0)
        {
            return RenderCurrent();
        }

        _index = (_index + 1) % _files.Count;
        return RenderCurrent();
    }

    public string Previous()
    {
        if (_files.Count == 0)
        {
            return RenderCurrent();
        }

        _index = (_index - 1 + _files.Count) % _files.Count;
        return RenderCurrent();
    }

    public bool TrySelect(int oneBasedIndex, out string rendered)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _files.Count)
        {
            rendered = _files.Count == 0
                ? "No review diff is available yet. Run /review first."
                : $"Diff index must be between 1 and {_files.Count}.";
            return false;
        }

        _index = oneBasedIndex - 1;
        rendered = RenderCurrent();
        return true;
    }

    private static List<ReviewDiffFile> ParseGitDiff(string summary)
    {
        var lines = NormalizeLines(summary);
        var files = new List<ReviewDiffFile>();
        string? currentPath = null;
        var content = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                currentPath = ParseGitPath(line);
            }

            if (currentPath is not null)
            {
                content.AppendLine(line);
            }
        }

        Flush();
        return files;

        void Flush()
        {
            if (currentPath is null)
            {
                content.Clear();
                return;
            }

            var text = content.ToString().TrimEnd();
            files.Add(new ReviewDiffFile(currentPath, text, CountHunks(text)));
            currentPath = null;
            content.Clear();
        }
    }

    private static List<ReviewDiffFile> ParseSnapshotDiff(string summary)
    {
        var lines = NormalizeLines(summary);
        var files = new List<ReviewDiffFile>();
        string? currentPath = null;
        var content = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("path: ", StringComparison.Ordinal))
            {
                Flush();
                currentPath = line["path: ".Length..].Trim();
            }

            if (currentPath is not null)
            {
                content.AppendLine(line);
            }
        }

        Flush();
        return files;

        void Flush()
        {
            if (currentPath is null)
            {
                content.Clear();
                return;
            }

            var text = content.ToString().TrimEnd();
            files.Add(new ReviewDiffFile(currentPath, text, Math.Max(1, CountSnapshotSections(text))));
            currentPath = null;
            content.Clear();
        }
    }

    private static IReadOnlyList<string> NormalizeLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string ParseGitPath(string line)
    {
        var marker = " b/";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? line["diff --git ".Length..].Trim() : line[(index + marker.Length)..].Trim();
    }

    private static int CountHunks(string content)
        => NormalizeLines(content).Count(line => line.StartsWith("@@", StringComparison.Ordinal));

    private static int CountSnapshotSections(string content)
        => NormalizeLines(content).Count(line => line.StartsWith("change_state:", StringComparison.Ordinal));

    private static string Clip(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "\n[truncated]";
}

internal sealed record ReviewDiffFile(string Path, string Content, int HunkCount);
