using System.Net;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

internal sealed class OpenAiCompatibleClient : ModelClientBase, IModelAdapter
{
    private ToolCallingMode? _probedMode;

    public OpenAiCompatibleClient(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
        : base(httpClient, config, profile)
    {
    }

    public ModelAdapterCapabilities AdapterCapabilities { get; } = new(
        new NativeToolSupport(true, true),
        new JsonModeSupport(true),
        new StreamingToolSupport(true));

    public async Task<ModelAdapterTurnResult> CompleteTurnAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        var mode = request.ToolCallingMode;
        if (mode == ToolCallingMode.Auto)
        {
            mode = Profile.ProbeToolCalling
                ? await ProbeNativeToolModeAsync(request, ct)
                : ToolCallingMode.NativeNonStreamingTools;
        }

        if (mode == ToolCallingMode.NativeStreamingTools)
        {
            return await CompleteNativeStreamingAsync(request, ct);
        }

        if (mode == ToolCallingMode.NativeNonStreamingTools)
        {
            try
            {
                return await CompleteNativeNonStreamingAsync(request, ct);
            }
            catch (InvalidOperationException) when (request.ToolCallingMode == ToolCallingMode.Auto)
            {
                return await CompleteJsonFallbackAsync(request, ct);
            }
        }

        return await CompleteJsonFallbackAsync(request, ct);
    }

    protected override async Task<ModelTurnResult> CompleteCoreAsync(ModelTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.OpenAiCompatiblePath);
        EnsureEndpointAllowed(endpoint);
        var initialMode = ResolveSystemPromptMode();
        var (statusCode, raw) = await SendWithPromptAndResponseFormatFallbackAsync(
            initialMode,
            (useResponseFormat, mode, token) => SendOpenAiRequestAsync(endpoint, request, useResponseFormat, mode, token),
            ct);

        if (!IsSuccessStatusCode(statusCode))
        {
            throw new InvalidOperationException(BuildHttpError("OpenAI-compatible", statusCode, endpoint, raw));
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? request.Model : request.Model;

        int? prompt = null;
        int? completion = null;
        int? total = null;

        if (root.TryGetProperty("usage", out var usageEl))
        {
            prompt = TryReadUsageToken(usageEl, "prompt_tokens");
            completion = TryReadUsageToken(usageEl, "completion_tokens");
            total = TryReadUsageToken(usageEl, "total_tokens");
        }

        return new ModelTurnResult(content, model, prompt, completion, total, raw);
    }

    private async Task<ModelAdapterTurnResult> CompleteJsonFallbackAsync(ModelAdapterTurnRequest request, CancellationToken ct)
        => await CompleteJsonReActFallbackAsync(request, ct);

    private async Task<ModelAdapterTurnResult> CompleteNativeNonStreamingAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.OpenAiCompatiblePath);
        EnsureEndpointAllowed(endpoint);
        var initialMode = ResolveSystemPromptMode();
        var (statusCode, raw) = await SendWithSystemPromptFallbackAsync(
            initialMode,
            (mode, token) => SendNativeOpenAiRequestAsync(endpoint, request, mode, stream: false, token),
            ct);

        if (!IsSuccessStatusCode(statusCode))
        {
            throw new InvalidOperationException(BuildNativeToolError("OpenAI-compatible native tools", statusCode, endpoint, raw));
        }

        var parsed = OpenAiCompatibleToolCallParser.ParseNonStreaming(raw, request.Model, ToolCallingMode.NativeNonStreamingTools);
        return parsed;
    }

    private async Task<ModelAdapterTurnResult> CompleteNativeStreamingAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.OpenAiCompatiblePath);
        EnsureEndpointAllowed(endpoint);
        var raw = await SendNativeStreamingRequestAsync(endpoint, request, ResolveSystemPromptMode(), ct);
        return OpenAiCompatibleToolCallParser.ParseStreaming(raw, request.Model);
    }

    private async Task<ToolCallingMode> ProbeNativeToolModeAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        if (_probedMode is { } cached)
        {
            return cached;
        }

        var probeTool = new ProbeTool();
        var probeRequest = request with
        {
            Messages = new[] { new ModelMessage("user", "Call the evoloop_probe_noop tool with {\"ok\":true}.") },
            InternalMessages = new InternalMessage[] { new UserMessage("Call the evoloop_probe_noop tool with {\"ok\":true}.") },
            Tools = new ITool[] { probeTool },
            MaxTokens = Math.Min(request.MaxTokens, 128),
            ToolCallingMode = ToolCallingMode.NativeNonStreamingTools
        };

        try
        {
            var result = await CompleteNativeNonStreamingAsync(probeRequest, ct);
            _probedMode = result.AssistantMessage.ToolCalls.Any(call =>
                call.Name.Value.Equals(probeTool.Name, StringComparison.OrdinalIgnoreCase))
                ? ToolCallingMode.NativeNonStreamingTools
                : ToolCallingMode.JsonReActFallback;
        }
        catch
        {
            _probedMode = ToolCallingMode.JsonReActFallback;
        }

        return _probedMode.Value;
    }

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendNativeOpenAiRequestAsync(
        Uri endpoint,
        ModelAdapterTurnRequest request,
        SystemPromptDeliveryMode mode,
        bool stream,
        CancellationToken ct)
    {
        var payload = new
        {
            model = request.Model,
            stream,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            messages = BuildNativeMessages(request, mode),
            tools = BuildOpenAiTools(request.Tools),
            tool_choice = "auto"
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonBody(payload)
        };

        using var response = await HttpClient.SendAsync(httpRequest, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, raw);
    }

    private async Task<string> SendNativeStreamingRequestAsync(
        Uri endpoint,
        ModelAdapterTurnRequest request,
        SystemPromptDeliveryMode mode,
        CancellationToken ct)
    {
        var payload = new
        {
            model = request.Model,
            stream = true,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            messages = BuildNativeMessages(request, mode),
            tools = BuildOpenAiTools(request.Tools),
            tool_choice = "auto"
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonBody(payload)
        };

        using var response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!IsSuccessStatusCode(response.StatusCode))
        {
            throw new InvalidOperationException(BuildNativeToolError("OpenAI-compatible streaming native tools", response.StatusCode, endpoint, raw));
        }

        return raw;
    }

    private static List<object> BuildNativeMessages(ModelAdapterTurnRequest request, SystemPromptDeliveryMode mode)
    {
        var list = new List<object>();
        if (mode == SystemPromptDeliveryMode.System || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "system", content = request.SystemPrompt });
        }

        if (mode == SystemPromptDeliveryMode.UserMessage || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "user", content = BuildSystemPromptAsUserMessage(request.SystemPrompt) });
        }

        foreach (var message in request.Messages)
        {
            if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                message.ToolCalls is { Count: > 0 })
            {
                list.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrWhiteSpace(message.Content) ? null : message.Content,
                    tool_calls = message.ToolCalls.Select(call => new
                    {
                        id = call.Id.Value,
                        type = "function",
                        function = new
                        {
                            name = call.Name.Value,
                            arguments = call.Arguments.GetRawText()
                        }
                    }).ToArray()
                });
                continue;
            }

            if (message.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    list.Add(new { role = "user", content = "OBSERVATION: " + message.Content });
                    continue;
                }

                list.Add(new
                {
                    role = "tool",
                    tool_call_id = message.ToolCallId,
                    content = message.Content
                });
                continue;
            }

            list.Add(new { role = message.Role, content = message.Content });
        }

        return list;
    }

    private static object[] BuildOpenAiTools(IEnumerable<ITool> tools)
    {
        return tools
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Schema.Description,
                    parameters = ToolSchemaJsonSchemaConverter.ToJsonSchema(tool)
                }
            })
            .Cast<object>()
            .ToArray();
    }

    private static string BuildNativeToolError(string label, HttpStatusCode statusCode, Uri endpoint, string raw)
    {
        var baseMessage = BuildHttpError(label, statusCode, endpoint, raw);
        if (raw.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("function", StringComparison.OrdinalIgnoreCase))
        {
            return baseMessage + " Native tool calling appears unsupported or rejected by this provider.";
        }

        return baseMessage;
    }

    private static List<object> BuildMessages(ModelTurnRequest request, SystemPromptDeliveryMode mode)
    {
        var list = new List<object>();
        if (mode == SystemPromptDeliveryMode.System || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "system", content = request.SystemPrompt });
        }

        if (mode == SystemPromptDeliveryMode.UserMessage || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "user", content = BuildSystemPromptAsUserMessage(request.SystemPrompt) });
        }

        list.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));
        return list;
    }

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendOpenAiRequestAsync(
        Uri endpoint,
        ModelTurnRequest request,
        bool useResponseFormat,
        SystemPromptDeliveryMode mode,
        CancellationToken ct)
    {
        var payload = new
        {
            model = request.Model,
            stream = false,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            messages = BuildMessages(request, mode),
            response_format = useResponseFormat ? new { type = "json_object" } : null
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonBody(payload)
        };

        using var response = await HttpClient.SendAsync(httpRequest, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, raw);
    }

}
