using System.Text.Json;

namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private static InternalMessage ToInternalMessage(ModelMessage message)
    {
        return message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? new AssistantMessage(new AssistantContentBlock[] { new TextBlock(message.Content) }, message.Content)
            : new UserMessage(message.Content);
    }

    private static AgentDecision DecisionFromAssistantMessage(AssistantMessage assistant)
    {
        if (assistant.Kind == AssistantMessageKind.Invalid)
        {
            return AgentDecision.Invalid(assistant.Error ?? "Invalid model response.");
        }

        var toolCall = assistant.ToolCalls.FirstOrDefault();
        if (toolCall is not null)
        {
            return DecisionFromToolCall(toolCall);
        }

        if (assistant.Kind == AssistantMessageKind.Clarify)
        {
            return new AgentDecision(
                AgentDecisionType.Clarify,
                string.Empty,
                default,
                string.Empty,
                assistant.Text);
        }

        if (assistant.Kind == AssistantMessageKind.Final || !string.IsNullOrWhiteSpace(assistant.Text))
        {
            return AgentDecision.Final(assistant.Text);
        }

        return AgentDecision.Invalid("Model response did not contain text or tool calls.");
    }

    private static AgentDecision DecisionFromToolCall(ToolCallBlock toolCall)
    {
        return new AgentDecision(
            AgentDecisionType.Tool,
            toolCall.Name.Value,
            toolCall.Arguments,
            toolCall.Reason ?? "model tool call",
            string.Empty,
            toolCall.Id.Value);
    }

    private static void EnqueueRemainingToolCalls(
        Queue<PendingToolTurn> pendingToolTurns,
        AssistantMessage assistant,
        string modelContent,
        ToolCallingMode mode)
    {
        var calls = assistant.ToolCalls;
        if (calls.Count <= 1)
        {
            return;
        }

        foreach (var call in calls.Skip(1))
        {
            pendingToolTurns.Enqueue(new PendingToolTurn(
                DecisionFromToolCall(call),
                assistant,
                modelContent,
                mode));
        }
    }

    private static string AssistantToLegacyContent(AssistantMessage assistant)
    {
        if (!string.IsNullOrWhiteSpace(assistant.RawContent))
        {
            return assistant.RawContent!;
        }

        if (assistant.ToolCalls.Count > 0)
        {
            var first = assistant.ToolCalls[0];
            return JsonSerializer.Serialize(new
            {
                type = "tool",
                tool = first.Name.Value,
                reason = first.Reason ?? "model tool call",
                arguments = first.Arguments
            });
        }

        return assistant.Text;
    }

    private static void AppendAssistantHistory(
        List<ModelMessage> history,
        List<InternalMessage> internalHistory,
        AssistantMessage assistant,
        string modelContent)
    {
        history.Add(new ModelMessage(
            "assistant",
            modelContent,
            ToolCalls: assistant.ToolCalls.Count > 0 ? assistant.ToolCalls : null));
        internalHistory.Add(assistant);
    }

    private static void AppendUserHistory(
        List<ModelMessage> history,
        List<InternalMessage> internalHistory,
        string content)
    {
        history.Add(new ModelMessage("user", content));
        internalHistory.Add(new UserMessage(content));
    }

    private static void AppendToolResultHistory(
        List<ModelMessage> history,
        List<InternalMessage> internalHistory,
        AgentDecision decision,
        ToolResultMessage toolResultMessage,
        ToolCallingMode toolCallingMode,
        string legacyObservation,
        int maxChars)
    {
        if (IsNativeToolMode(toolCallingMode))
        {
            history.Add(new ModelMessage(
                "tool",
                toolResultMessage.ToObservationText(maxChars),
                decision.ToolCallId,
                decision.ToolName));
        }
        else
        {
            history.Add(new ModelMessage("user", legacyObservation));
        }

        internalHistory.Add(toolResultMessage);
    }

    private static ToolResultMessage CreateToolResultMessage(AgentDecision decision, ToolResult result, int maxChars)
    {
        var call = new ToolCallBlock(
            string.IsNullOrWhiteSpace(decision.ToolCallId)
                ? ToolCallId.CreateFallback()
                : new ToolCallId(decision.ToolCallId!),
            new ToolName(decision.ToolName),
            decision.Arguments,
            decision.Reason);

        return ToolResultMessage.FromToolResult(call, result);
    }

    private static bool IsNativeToolMode(ToolCallingMode mode)
    {
        return mode is ToolCallingMode.NativeNonStreamingTools or ToolCallingMode.NativeStreamingTools;
    }

}
