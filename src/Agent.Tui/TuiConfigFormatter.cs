using System.Text;

namespace Agent.Tui;

internal static class TuiConfigFormatter
{
    public static string Format(TuiRuntimeInfo runtime)
    {
        var sb = new StringBuilder();
        AppendHeader(runtime, sb);
        AppendConnection(runtime, sb);
        AppendModelProfiles(runtime, sb);
        AppendSafety(runtime, sb);
        AppendToolCalling(runtime, sb);
        AppendLimits(runtime, sb);
        AppendPrompts(runtime, sb);
        AppendStorage(runtime, sb);
        return sb.ToString().TrimEnd();
    }

    public static string FormatPath(TuiRuntimeInfo runtime)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "Config paths",
            $"|- loaded config: {runtime.ConfigPath}",
            $"`- default config: {runtime.DefaultConfigPath}"
        });
    }

    private static void AppendHeader(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Config");
        sb.AppendLine("|- files");
        sb.AppendLine($"|  |- loaded config: {runtime.ConfigPath}");
        sb.AppendLine($"|  `- default config: {runtime.DefaultConfigPath}");
        sb.AppendLine("|- runtime");
        sb.AppendLine($"|  |- mode: {runtime.ModeLabel}");
        sb.AppendLine($"|  |- auth: {(runtime.ApiAuthConfigured ? "present" : "missing")}");
        sb.AppendLine($"|  `- gateway: {(runtime.ModelReachable ? "reachable" : "unreachable")}");
        sb.AppendLine("`- active");
        sb.AppendLine($"   |- profile: {runtime.Profile}");
        sb.AppendLine($"   `- model: {runtime.ModelId}");
        sb.AppendLine();
    }

    private static void AppendConnection(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Connection");
        sb.AppendLine($"|- baseUrl: {runtime.ApiBaseUrl}");
        sb.AppendLine($"|- openAiCompatiblePath: {runtime.OpenAiCompatiblePath}");
        sb.AppendLine($"|- customPath: {runtime.CustomPath}");
        sb.AppendLine($"|- apiKeyEnvVar: {runtime.ApiKeyEnvVar}");
        sb.AppendLine($"|- modelConfigured: {runtime.ModelConfigured}");
        sb.AppendLine($"`- modelReachable: {runtime.ModelReachable}");
        sb.AppendLine();
    }

    private static void AppendModelProfiles(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Model Profiles");
        sb.AppendLine($"|- active: {runtime.Profile}");
        sb.AppendLine($"|- provider: {runtime.ModelProvider}");
        sb.AppendLine($"|- model: {runtime.ModelId}");
        sb.AppendLine($"|- configured: {JoinOrNone(runtime.ModelProfiles)}");
        sb.AppendLine($"`- fallbackOrder: {JoinOrNone(runtime.ProfileFallbackOrder)}");
        sb.AppendLine();
    }

    private static void AppendSafety(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Safety");
        sb.AppendLine($"|- approvalMode: {runtime.ApprovalMode}");
        sb.AppendLine($"`- offlineStrict: {runtime.OfflineStrict}");
        sb.AppendLine();
    }

    private static void AppendToolCalling(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Tool Calling");
        sb.AppendLine($"|- mode: {runtime.ToolCallingMode}");
        sb.AppendLine("|- JsonReActFallback uses one tool call per model turn.");
        sb.AppendLine("`- Native tool-call modes remain opt-in per model profile.");
        sb.AppendLine();
    }

    private static void AppendLimits(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Limits / Advanced");
        sb.AppendLine($"|- maxSteps: {runtime.MaxSteps}");
        sb.AppendLine($"|- invalidResponses: max={runtime.MaxInvalidModelResponses}; switchAfter={runtime.InvalidResponsesBeforeProfileSwitch}");
        sb.AppendLine($"|- finalWithoutTools: max={runtime.MaxConsecutiveFinalWithoutTools}; switchAfter={runtime.FinalWithoutToolsBeforeProfileSwitch}");
        sb.AppendLine($"|- toolTimeoutSeconds: {runtime.ToolTimeoutSeconds}");
        sb.AppendLine($"|- maxOutputBytes: {runtime.MaxOutputBytes}");
        sb.AppendLine($"|- outputTokensClamp: {runtime.ModelMinOutputTokens}..{runtime.ModelMaxOutputTokens}");
        sb.AppendLine($"|- temperatureClamp: {runtime.ModelMinTemperature}..{runtime.ModelMaxTemperature}");
        sb.AppendLine($"|- history: messages={runtime.HistoryMaxMessages}; chars={runtime.HistoryMaxChars}; keepTail={runtime.HistoryKeepTailMessages}");
        sb.AppendLine($"|- observationMaxChars: {runtime.ObservationMaxChars}");
        sb.AppendLine($"|- contextBudgets: docs={runtime.ContextProjectDocMaxChars}; file={runtime.ContextFileExcerptMaxChars}; observations={runtime.ContextObservationBudgetChars}; summary={runtime.ContextHistorySummaryChars}");
        sb.AppendLine($"`- memory: enabled={runtime.MemoryEnabled}; runs={runtime.MemoryMaxRuns}; chars={runtime.MemoryContextMaxChars}");
        sb.AppendLine();
    }

    private static void AppendPrompts(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Prompts");
        var paths = runtime.PromptPaths.ToArray();
        for (var i = 0; i < paths.Length; i++)
        {
            var marker = i == paths.Length - 1 ? "`-" : "|-";
            sb.AppendLine($"{marker} {paths[i]}");
        }
        sb.AppendLine();
    }

    private static void AppendStorage(TuiRuntimeInfo runtime, StringBuilder sb)
    {
        sb.AppendLine("Storage");
        sb.AppendLine($"|- workspaceWritable: {runtime.WorkspaceWritable}");
        sb.AppendLine($"|- git: {runtime.GitAvailable}");
        sb.AppendLine($"|- rg: {runtime.RipgrepAvailable}");
        sb.AppendLine($"`- sqlite: {runtime.SqliteAvailable}");
    }

    private static string JoinOrNone(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return string.IsNullOrWhiteSpace(joined) ? "<none>" : joined;
    }
}
