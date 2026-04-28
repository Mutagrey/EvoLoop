using System.Text;

namespace Agent.Core;

internal sealed class DefaultPromptBuilder : IPromptBuilder
{
    public string BuildSystemPrompt(IReadOnlyCollection<ITool> tools, ToolContext context)
    {
        var toolList = tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("You are EvoLoop, an offline-first coding agent harness.");
        sb.AppendLine("Primary objective: complete the user's task by using tools and producing concrete workspace outcomes.");
        sb.AppendLine("Execution model: ReAct loop (analyze -> one tool call -> observe -> repeat).");
        sb.AppendLine("The model proposes actions only. The harness decides whether actions are allowed and performs them.");
        sb.AppendLine("Return EXACTLY one JSON object.");
        sb.AppendLine("For tool call: {\"type\":\"tool\",\"tool\":\"tool_name\",\"reason\":\"why\",\"arguments\":{...}}");
        sb.AppendLine("For final: {\"type\":\"final\",\"message\":\"...\"}");
        sb.AppendLine("For clarify: {\"type\":\"clarify\",\"message\":\"...\"}");
        sb.AppendLine("Rules:");
        sb.AppendLine("- No markdown fences, no prose outside JSON.");
        sb.AppendLine("- Include all required fields for the selected tool.");
        sb.AppendLine("- Prefer specialized tools over exec_shell.");
        sb.AppendLine("- Do not claim actions unless tool observations confirm them.");
        sb.AppendLine("- The environment may be Windows-first, offline or restricted, and may not allow admin rights or dependency installation.");
        sb.AppendLine($"- Current execution mode is '{context.ExecutionMode}' and approval mode is '{context.ApprovalMode}'.");

        if (context.ExecutionMode == AgentExecutionMode.Plan)
        {
            sb.AppendLine("- Plan mode forbids workspace mutations, shell execution, staging, and commits.");
        }
        else if (context.ExecutionMode == AgentExecutionMode.Review)
        {
            sb.AppendLine("- Review mode is inspection-only. Focus on risks, regressions, and missing tests.");
        }

        sb.AppendLine("Available tools:");
        foreach (var tool in toolList)
        {
            sb.Append($"- {tool.Name}: {tool.Schema.Description} | risk={tool.Metadata.RiskLevel} | category={tool.Metadata.Category}");
            if (tool.Metadata.IsFallbackOnly)
            {
                sb.Append(" | fallback-only");
            }

            sb.AppendLine();
            if (tool.Schema.RequiredFields.Count > 0)
            {
                sb.AppendLine($"  required: {string.Join(", ", tool.Schema.RequiredFields)}");
            }
        }

        sb.AppendLine("For action-oriented tasks (create/edit/delete/run/git), call a tool before returning final.");
        sb.AppendLine("If a required field is unknown, first use discovery tools like fs_list, search_lexical, fs_read, or git_diff.");
        return sb.ToString().TrimEnd();
    }
}
