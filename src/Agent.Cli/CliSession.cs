using System.Text;
using Agent.Core;
using Agent.Hosting;
using Agent.Tools;

namespace Agent.Cli;

internal static class CliSession
{
    public static async Task RunReplAsync(
        IReadOnlyList<ITool> tools,
        AnsiRenderer renderer,
        AgentTaskRunner taskRunner,
        AgentConfig config,
        string workspace,
        string profile,
        IWorkspaceMemoryStore memoryStore,
        RuntimeCapabilities capabilities,
        IPatchService patchService)
    {
        renderer.WriteHeader("EvoLoop Agent CLI");
        renderer.WritePanel(
            "Session",
            $"Workspace: {workspace}\nModel profile: {profile}\nMode: {capabilities.ModeLabel}\nCommands: /task, /plan, /review, /status, /tools, /history, /memory, /cmdlog, /config, /doctor, /undo, /exit\nRecall: !N");

        AgentRunResult? lastRun = null;
        AgentRunResult? lastPlan = null;
        var commandHistory = await ReplCommandHistory.OpenAsync(workspace, 300, CancellationToken.None);

        while (true)
        {
            Console.Write("\nagent> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                break;
            }

            input = input.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.StartsWith("!", StringComparison.Ordinal))
            {
                if (!commandHistory.TryResolve(input, out var recalled))
                {
                    renderer.WriteWarn("Unknown history index. Use /cmdlog to list saved commands.");
                    continue;
                }

                renderer.WriteInfo($"Recalled: {recalled}");
                input = recalled;
            }

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.StartsWith("/task ", StringComparison.OrdinalIgnoreCase))
            {
                var task = input[6..].Trim();
                if (string.IsNullOrWhiteSpace(task))
                {
                    renderer.WriteWarn("Task text is empty.");
                    continue;
                }

                await commandHistory.AddAsync(task, CancellationToken.None);
                lastRun = await RunTaskAsync(taskRunner, renderer, task, workspace, profile, capabilities, AgentExecutionMode.Run, config.Safety.DefaultApprovalMode);
                continue;
            }

            if (input.StartsWith("/plan ", StringComparison.OrdinalIgnoreCase))
            {
                var task = input[6..].Trim();
                if (string.IsNullOrWhiteSpace(task))
                {
                    renderer.WriteWarn("Plan task text is empty.");
                    continue;
                }

                await commandHistory.AddAsync(input, CancellationToken.None);
                lastPlan = await RunTaskAsync(taskRunner, renderer, task, workspace, profile, capabilities, AgentExecutionMode.Plan, ApprovalPolicyMode.ReadOnly);
                continue;
            }

            if (input.Equals("/plan", StringComparison.OrdinalIgnoreCase))
            {
                if (lastPlan is null)
                {
                    renderer.WriteInfo("No plan has been generated yet.");
                }
                else
                {
                    renderer.WritePanel("Last Plan", lastPlan.FinalMessage);
                }
                continue;
            }

            if (input.StartsWith("/review", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = input.Length > 7 ? input[7..].Trim() : null;
                lastRun = await RunTaskAsync(taskRunner, renderer, AgentTaskRunner.BuildReviewTask(suffix), workspace, profile, capabilities, AgentExecutionMode.Review, ApprovalPolicyMode.ReadOnly);
                continue;
            }

            if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                if (lastRun is null)
                {
                    renderer.WriteInfo("No task executed yet.");
                }
                else
                {
                    renderer.WritePanel(
                        "Status",
                        $"Session: {lastRun.SessionId}\nSuccess: {lastRun.Success}\nSteps: {lastRun.Steps}");
                }
                continue;
            }

            if (input.Equals("/tools", StringComparison.OrdinalIgnoreCase))
            {
                var body = string.Join(Environment.NewLine, tools.OrderBy(t => t.Name).Select(t => $"- {t.Name}: {t.Schema.Description} | risk={t.Metadata.RiskLevel} | category={t.Metadata.Category}"));
                renderer.WritePanel("Tools", body);
                continue;
            }

            if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
            {
                if (lastRun is null)
                {
                    renderer.WriteInfo("No run history available.");
                }
                else
                {
                    var body = new StringBuilder();
                    foreach (var step in lastRun.StepTrace)
                    {
                        body.AppendLine($"#{step.StepNumber} {step.ToolName} success={step.Success} duration={step.DurationMs}ms");
                        if (!string.IsNullOrWhiteSpace(step.Error))
                        {
                            body.AppendLine($"  error: {step.Error}");
                        }
                    }

                    renderer.WritePanel("Last Run History", body.ToString());
                }
                continue;
            }

            if (input.Equals("/config", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WritePanel("Config", FormatConfig(config));
                continue;
            }

            if (input.Equals("/doctor", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WritePanel("Capabilities", capabilities.ToDisplayText());
                continue;
            }

            if (input.Equals("/memory", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.Runtime.MemoryEnabled)
                {
                    renderer.WriteInfo("Memory is disabled in runtime config.");
                    continue;
                }

                var memory = await memoryStore.LoadContextAsync(workspace, "workspace overview", CancellationToken.None);
                if (string.IsNullOrWhiteSpace(memory.Content))
                {
                    renderer.WriteInfo("No workspace memory available yet.");
                }
                else
                {
                    renderer.WritePanel("Workspace Memory", memory.Content);
                }

                continue;
            }

            if (input.Equals("/undo", StringComparison.OrdinalIgnoreCase))
            {
                var undoResult = await patchService.UndoLastAsync(workspace, CancellationToken.None);
                renderer.WritePanel(undoResult.Success ? "Undo" : "Undo Failed", undoResult.Message);
                continue;
            }

            if (input.Equals("/cmdlog", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WritePanel("Saved Commands", commandHistory.FormatRecent(30));
                continue;
            }

            if (input.Equals("/approve", StringComparison.OrdinalIgnoreCase) || input.Equals("/deny", StringComparison.OrdinalIgnoreCase))
            {
                renderer.WriteInfo("Approvals are handled inline when a risky action is requested.");
                continue;
            }

            await commandHistory.AddAsync(input, CancellationToken.None);
            lastRun = await RunTaskAsync(taskRunner, renderer, input, workspace, profile, capabilities, AgentExecutionMode.Run, config.Safety.DefaultApprovalMode);
        }

        renderer.WriteInfo("Goodbye.");
    }

    public static async Task<AgentRunResult> RunTaskAsync(
        AgentTaskRunner taskRunner,
        AnsiRenderer renderer,
        string task,
        string workspace,
        string profile,
        RuntimeCapabilities capabilities,
        AgentExecutionMode executionMode,
        ApprovalPolicyMode approvalMode)
    {
        if (!capabilities.CanRunAgentTasks)
        {
            if (executionMode == AgentExecutionMode.Review)
            {
                var review = await taskRunner.RunAsync(task, profile, executionMode, approvalMode, null, CancellationToken.None);
                renderer.WritePanel("Review", review.LocalReviewSummary ?? review.Result.FinalMessage);
                return review.Result;
            }

            renderer.WriteError(
                $"Agent task execution is unavailable in '{capabilities.ModeLabel}' mode. Run 'agent doctor' to inspect gateway connectivity and environment restrictions.");

            return new AgentRunResult(
                false,
                $"Task was not started because model execution is unavailable. {capabilities.ModelStatus}.",
                0,
                "not-started",
                Array.Empty<SessionStep>());
        }

        using var observer = new SpinnerObserver(renderer);

        renderer.WritePanel("Task", task);

        var outcome = await taskRunner.RunAsync(task, profile, executionMode, approvalMode, observer, CancellationToken.None);
        var result = outcome.Result;

        await observer.WriteActivitySummaryAsync(workspace, CancellationToken.None);

        renderer.WritePanel(
            result.Success ? "Done" : "Incomplete",
            $"Session: {result.SessionId}\nSteps: {result.Steps}\n\n{result.FinalMessage}");

        return result;
    }
    private static string FormatConfig(AgentConfig config)
    {
        var configPath = AgentConfigLoader.GetDefaultConfigPath();
        var models = string.Join(", ", config.Models.Keys.OrderBy(x => x));
        var hosts = config.Safety.AllowedNetworkHosts.Count == 0 ? "<none>" : string.Join(", ", config.Safety.AllowedNetworkHosts);
        var apiKeyInEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.Api.ApiKeyEnvVar));
        var apiKeyInConfig = !string.IsNullOrWhiteSpace(config.Api.ApiKey);
        var apiKeyState = (apiKeyInEnv || apiKeyInConfig) ? "present" : "missing";
        var apiKeySource = apiKeyInEnv ? "env" : (apiKeyInConfig ? "config" : "none");
        var fallbackOrder = config.Runtime.ProfileFallbackOrder.Count == 0 ? "<none>" : string.Join(", ", config.Runtime.ProfileFallbackOrder);
        return $"Path: {configPath}\nModel profiles: {models}\nProfileFallbackOrder: {fallbackOrder}\nAPI URL: {config.Api.BaseUrl}\nOpenAI Path: {config.Api.OpenAiCompatiblePath}\nCustom Path: {config.Api.CustomPath}\nSystemPromptMode: {config.Api.SystemPromptMode}\nSystemPromptFallbackToUserMessage: {config.Api.SystemPromptFallbackToUserMessage}\nApiKeyEnvVar: {config.Api.ApiKeyEnvVar}\nApiKey: {apiKeyState} ({apiKeySource})\nOfflineStrict: {config.Safety.OfflineStrictMode}\nAllowedHosts: {hosts}\nMemoryEnabled: {config.Runtime.MemoryEnabled}\nAdaptivePrompting: {config.Runtime.AdaptivePromptingEnabled}";
    }
}
