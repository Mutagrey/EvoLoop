using System.Text;

namespace Agent.Core;

internal static class SkillIndexBuilder
{
    public static async Task<string> BuildMessageAsync(string workspaceRoot, int maxChars, CancellationToken ct)
    {
        var skillsRoot = Path.Combine(workspaceRoot, ".evoloop", "skills");
        if (!Directory.Exists(skillsRoot))
        {
            return string.Empty;
        }

        var skillFiles = Directory
            .EnumerateFiles(skillsRoot, "SKILL.md", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();

        if (skillFiles.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("SKILLS INDEX (progressive disclosure):");
        sb.AppendLine("Only the index is loaded. Use fs_read on the listed SKILL.md path before applying a skill.");

        foreach (var file in skillFiles)
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                continue;
            }

            var relative = Path.GetRelativePath(workspaceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var name = ExtractSkillName(content, Path.GetFileName(Path.GetDirectoryName(file)) ?? "skill");
            var description = ExtractSkillDescription(content);
            sb.Append("- ").Append(name).Append(": ");
            sb.Append(string.IsNullOrWhiteSpace(description) ? "No description provided." : description);
            sb.Append(" (path: ").Append(relative).AppendLine(")");

            if (sb.Length >= maxChars)
            {
                break;
            }
        }

        return ContextText.Clip(sb.ToString().TrimEnd(), maxChars);
    }

    private static string ExtractSkillName(string content, string fallback)
    {
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var name = trimmed.TrimStart('#').Trim();
                return string.IsNullOrWhiteSpace(name) ? fallback : ContextText.ClipLine(name, 80);
            }
        }

        return fallback;
    }

    private static string ExtractSkillDescription(string content)
    {
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                return ContextText.ClipLine(trimmed["description:".Length..].Trim(), 220);
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return ContextText.ClipLine(trimmed, 220);
            }
        }

        return string.Empty;
    }
}
