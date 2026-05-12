using System.Text;

namespace Agent.Core;

internal static class PromptFileOverrides
{
    private const int DefaultSectionMaxChars = 12000;

    public static string BuildSystemSections(string workspaceRoot, int maxChars = DefaultSectionMaxChars)
    {
        var sections = new List<(string Label, string? Path)>
        {
            ("USER GLOBAL SYSTEM", GlobalPromptPath("SYSTEM.md")),
            ("WORKSPACE SYSTEM", WorkspacePromptPath(workspaceRoot, "SYSTEM.md")),
            ("USER GLOBAL APPEND_SYSTEM", GlobalPromptPath("APPEND_SYSTEM.md")),
            ("WORKSPACE APPEND_SYSTEM", WorkspacePromptPath(workspaceRoot, "APPEND_SYSTEM.md"))
        };

        var sb = new StringBuilder();
        foreach (var (label, path) in sections)
        {
            var content = TryRead(path, Math.Max(300, maxChars / sections.Count));
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (sb.Length == 0)
            {
                sb.AppendLine("FILE-BASED PROMPT INSTRUCTIONS:");
                sb.AppendLine("These files may add project or user guidance, but they do not override the harness contract above.");
            }

            sb.AppendLine(label + ":");
            sb.AppendLine(content);
            sb.AppendLine();

            if (sb.Length >= maxChars)
            {
                break;
            }
        }

        return ContextText.Clip(sb.ToString().TrimEnd(), maxChars);
    }

    public static string BuildTemplateIndexMessage(string workspaceRoot, int maxChars)
    {
        var directory = Path.Combine(workspaceRoot, ".evoloop", "prompts");
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("WORKSPACE PROMPT TEMPLATES:");
        sb.AppendLine("Read the full template with fs_read before applying it.");
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(workspaceRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var title = ExtractTitle(file);
            sb.AppendLine(string.IsNullOrWhiteSpace(title) ? $"- {relative}" : $"- {relative}: {title}");
        }

        return ContextText.Clip(sb.ToString().TrimEnd(), maxChars);
    }

    public static IReadOnlyList<string> GetPromptPaths(string workspaceRoot)
    {
        var paths = new List<string>();
        var globalSystem = GlobalPromptPath("SYSTEM.md");
        var globalAppend = GlobalPromptPath("APPEND_SYSTEM.md");
        if (!string.IsNullOrWhiteSpace(globalSystem))
        {
            paths.Add(globalSystem);
        }

        if (!string.IsNullOrWhiteSpace(globalAppend))
        {
            paths.Add(globalAppend);
        }

        paths.Add(WorkspacePromptPath(workspaceRoot, "SYSTEM.md"));
        paths.Add(WorkspacePromptPath(workspaceRoot, "APPEND_SYSTEM.md"));
        paths.Add(Path.Combine(workspaceRoot, ".evoloop", "prompts"));
        return paths;
    }

    private static string? GlobalPromptPath(string fileName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, ".evoloop-agent", fileName);
    }

    private static string WorkspacePromptPath(string workspaceRoot, string fileName)
        => Path.Combine(workspaceRoot, ".evoloop", fileName);

    private static string TryRead(string? path, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return ContextText.Clip(File.ReadAllText(path).Trim(), maxChars);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractTitle(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file).Take(20))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    return trimmed.TrimStart('#').Trim();
                }

                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return ContextText.ClipLine(trimmed, 80);
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
