using System.Text;

namespace Agent.Tui;

internal static class TuiSkillsFormatter
{
    public static string Format(string workspaceRoot, int maxSkills = 50)
    {
        var skillsRoot = Path.Combine(workspaceRoot, ".evoloop", "skills");
        if (!Directory.Exists(skillsRoot))
        {
            return "Skills\n`- No workspace skills found at .evoloop/skills.";
        }

        var files = Directory
            .EnumerateFiles(skillsRoot, "SKILL.md", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(maxSkills)
            .ToArray();

        if (files.Length == 0)
        {
            return "Skills\n`- No workspace skills found at .evoloop/skills.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Skills");
        for (var i = 0; i < files.Length; i++)
        {
            var marker = i == files.Length - 1 ? "`-" : "|-";
            var relative = Path.GetRelativePath(workspaceRoot, files[i]).Replace(Path.DirectorySeparatorChar, '/');
            var (name, description) = ReadSkill(files[i]);
            sb.Append(marker).Append(' ').Append(name).AppendLine();
            sb.Append(i == files.Length - 1 ? "   " : "|  ");
            sb.Append("|- path: ").AppendLine(relative);
            sb.Append(i == files.Length - 1 ? "   " : "|  ");
            sb.Append("`- ").AppendLine(string.IsNullOrWhiteSpace(description) ? "No description provided." : description);
        }

        return sb.ToString().TrimEnd();
    }

    private static (string Name, string Description) ReadSkill(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var fallback = Path.GetFileName(Path.GetDirectoryName(path)) ?? "skill";
            return (ExtractName(content, fallback), ExtractDescription(content));
        }
        catch
        {
            return (Path.GetFileName(Path.GetDirectoryName(path)) ?? "skill", "Failed to read SKILL.md.");
        }
    }

    private static string ExtractName(string content, string fallback)
    {
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var name = trimmed.TrimStart('#').Trim();
            return string.IsNullOrWhiteSpace(name) ? fallback : Clip(name, 80);
        }

        return fallback;
    }

    private static string ExtractDescription(string content)
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
                return Clip(trimmed["description:".Length..].Trim(), 220);
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return Clip(trimmed, 220);
            }
        }

        return string.Empty;
    }

    private static string Clip(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
