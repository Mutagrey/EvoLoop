using System.Text;

namespace Agent.Core;

internal sealed class DefaultContextBuilder : IContextBuilder
{
    public async Task<IReadOnlyList<ModelMessage>> BuildInitialMessagesAsync(
        AgentRunRequest request,
        ToolContext context,
        IWorkspaceMemoryStore memoryStore,
        CancellationToken ct)
    {
        var messages = new List<ModelMessage>();

        var docs = await ProjectSourceOfTruth.BuildMessageAsync(request.WorkspaceRoot, context.Config.Runtime.ContextProjectDocMaxChars, ct);
        if (!string.IsNullOrWhiteSpace(docs))
        {
            messages.Add(new ModelMessage("user", docs));
        }

        var skillIndex = await SkillIndexBuilder.BuildMessageAsync(request.WorkspaceRoot, context.Config.Runtime.ContextProjectDocMaxChars / 3, ct);
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
}
