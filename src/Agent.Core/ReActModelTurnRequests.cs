namespace Agent.Core;

public sealed partial class ReActAgentLoop
{
    private ModelAdapterTurnRequest CreateModelTurnRequest(
        string profileName,
        string modelName,
        string systemPrompt,
        IReadOnlyList<ModelMessage> history,
        IReadOnlyList<InternalMessage> internalHistory,
        ToolCallingMode toolCallingMode,
        string sessionId,
        int step)
    {
        return new ModelAdapterTurnRequest(
            profileName,
            modelName,
            systemPrompt,
            history,
            internalHistory,
            _tools.Values.ToList(),
            toolCallingMode,
            _profileSelection.GetTemperature(profileName),
            _profileSelection.GetMaxTokens(profileName),
            new Dictionary<string, string>
            {
                ["session_id"] = sessionId,
                ["step"] = step.ToString(),
                ["profile"] = profileName
            });
    }
}
