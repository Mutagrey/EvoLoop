using System.Text;

namespace Agent.Core;

internal static class ProjectSourceOfTruth
{
    private static readonly string[] Paths =
    {
        "AGENTS.md",
        "docs/ARCHITECTURE.md",
        "docs/OPERATING-MODES.md",
        "docs/STATUS.md"
    };

    public static async Task<string> BuildMessageAsync(string workspaceRoot, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROJECT INSTRUCTIONS AND SOURCE-OF-TRUTH DOCS:");

        foreach (var relativePath in Paths)
        {
            var fullPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, ct);
            content = ContextText.Clip(content.Trim(), Math.Max(300, maxChars / Paths.Length));
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            sb.AppendLine($"FILE: {relativePath}");
            sb.AppendLine(content);
            sb.AppendLine();

            if (sb.Length >= maxChars)
            {
                break;
            }
        }

        return ContextText.Clip(sb.ToString().TrimEnd(), maxChars);
    }
}
