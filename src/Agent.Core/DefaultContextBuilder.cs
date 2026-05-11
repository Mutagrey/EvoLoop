using System.Text;

namespace Agent.Core;

internal sealed class DefaultContextBuilder : IContextBuilder
{
    private static readonly string[] SourceOfTruthPaths =
    {
        "AGENTS.md",
        "docs/ARCHITECTURE.md",
        "docs/OPERATING-MODES.md",
        "docs/STATUS.md"
    };

    public async Task<IReadOnlyList<ModelMessage>> BuildInitialMessagesAsync(
        AgentRunRequest request,
        ToolContext context,
        IWorkspaceMemoryStore memoryStore,
        CancellationToken ct)
    {
        var messages = new List<ModelMessage>();

        var docs = await BuildProjectInstructionMessageAsync(request.WorkspaceRoot, context.Config.Runtime.ContextProjectDocMaxChars, ct);
        if (!string.IsNullOrWhiteSpace(docs))
        {
            messages.Add(new ModelMessage("user", docs));
        }

        var skillIndex = await BuildSkillIndexMessageAsync(request.WorkspaceRoot, context.Config.Runtime.ContextProjectDocMaxChars / 3, ct);
        if (!string.IsNullOrWhiteSpace(skillIndex))
        {
            messages.Add(new ModelMessage("user", skillIndex));
        }

        messages.Add(new ModelMessage("user", BuildRuntimeContextMessage(context)));
        messages.Add(new ModelMessage("user", $"TASK:\n{request.Task}"));

        if (context.Config.Runtime.MemoryEnabled)
        {
            var memoryContext = await memoryStore.LoadContextAsync(request.WorkspaceRoot, request.Task, ct);
            if (!string.IsNullOrWhiteSpace(memoryContext.Content))
            {
                messages.Add(new ModelMessage("user", memoryContext.Content));
            }
        }

        var frame = BuildExecutionFrame(request.ExecutionMode);
        if (!string.IsNullOrWhiteSpace(frame))
        {
            messages.Add(new ModelMessage("user", frame));
        }

        return messages;
    }

    private static async Task<string> BuildProjectInstructionMessageAsync(string workspaceRoot, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROJECT INSTRUCTIONS AND SOURCE-OF-TRUTH DOCS:");

        foreach (var relativePath in SourceOfTruthPaths)
        {
            var fullPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, ct);
            content = Clip(content.Trim(), Math.Max(300, maxChars / SourceOfTruthPaths.Length));
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

        return Clip(sb.ToString().TrimEnd(), maxChars);
    }

    private static async Task<string> BuildSkillIndexMessageAsync(string workspaceRoot, int maxChars, CancellationToken ct)
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

        return Clip(sb.ToString().TrimEnd(), maxChars);
    }

    private static string ExtractSkillName(string content, string fallback)
    {
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var name = trimmed.TrimStart('#').Trim();
                return string.IsNullOrWhiteSpace(name) ? fallback : ClipLine(name, 80);
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
                return ClipLine(trimmed["description:".Length..].Trim(), 220);
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return ClipLine(trimmed, 220);
            }
        }

        return string.Empty;
    }

    private static string BuildRuntimeContextMessage(ToolContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RUNTIME ENVIRONMENT:");
        sb.AppendLine($"- execution_mode: {context.ExecutionMode}");
        sb.AppendLine($"- approval_mode: {context.ApprovalMode}");
        sb.AppendLine($"- operating_mode: {context.Capabilities.ModeLabel}");
        sb.AppendLine($"- platform: {context.Capabilities.Platform}");
        sb.AppendLine($"- workspace_storage: {context.Capabilities.WorkspaceStatus}");
        sb.AppendLine($"- shell_available: {context.Capabilities.ShellAvailable}");
        sb.AppendLine($"- git_available: {context.Capabilities.GitAvailable}");
        sb.AppendLine($"- rg_available: {context.Capabilities.RipgrepAvailable}");
        sb.AppendLine($"- sqlite_available: {context.Capabilities.SqliteAvailable}");
        sb.AppendLine($"- model_configured: {context.Capabilities.ModelConfigured}");
        sb.AppendLine($"- model_reachable: {context.Capabilities.ModelReachable}");
        sb.AppendLine($"- auth_present: {context.Capabilities.AuthConfigured}");
        sb.AppendLine($"- model_status: {context.Capabilities.ModelStatus}");
        return sb.ToString().TrimEnd();
    }

    private static string BuildExecutionFrame(AgentExecutionMode mode)
    {
        return mode switch
        {
            AgentExecutionMode.Plan => "EXECUTION FRAME: Plan mode. Analyze and plan only. Do not mutate files, run shell commands, or stage/commit changes.",
            AgentExecutionMode.Review => "EXECUTION FRAME: Review mode. Inspect current changes, prioritize risks and regressions, and do not mutate the workspace.",
            _ => string.Empty
        };
    }

    private static string Clip(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..Math.Max(0, maxChars - 14)] + "\n[truncated]";
    }

    private static string ClipLine(string value, int maxChars)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine[..Math.Max(0, maxChars - 3)] + "...";
    }
}
