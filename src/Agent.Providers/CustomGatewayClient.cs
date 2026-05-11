using System.Net;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

internal sealed class CustomGatewayClient : ModelClientBase, IModelAdapter
{
    public CustomGatewayClient(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
        : base(httpClient, config, profile)
    {
    }

    public ModelAdapterCapabilities AdapterCapabilities => ModelAdapterCapabilities.JsonOnly;

    public async Task<ModelAdapterTurnResult> CompleteTurnAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        var result = await CompleteAsync(new ModelTurnRequest(
            request.ProfileName,
            request.Model,
            request.SystemPrompt,
            request.Messages,
            request.Temperature,
            request.MaxTokens,
            request.Metadata), ct);

        var assistant = JsonReActResponseParser.Parse(
            result.Content,
            request.Tools.Select(tool => tool.Name),
            allowPlainTextRecovery: true,
            mode: ToolCallingMode.JsonReActFallback);

        return new ModelAdapterTurnResult(
            assistant,
            result.Model,
            result.PromptTokens,
            result.CompletionTokens,
            result.TotalTokens,
            result.Raw,
            ToolCallingMode.JsonReActFallback);
    }

    protected override async Task<ModelTurnResult> CompleteCoreAsync(ModelTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.CustomPath);
        EnsureEndpointAllowed(endpoint);
        var initialMode = ResolveSystemPromptMode();
        var (statusCode, raw) = await SendWithPromptFallbackAsync(endpoint, request, initialMode, ct);

        if (!IsSuccessStatusCode(statusCode))
        {
            throw new InvalidOperationException(BuildHttpError("Custom gateway", statusCode, endpoint, raw));
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var content = ExtractContent(root);
        var model = ExtractModel(root, request.Model);

        int? prompt = null;
        int? completion = null;
        int? total = null;

        if (root.TryGetProperty("usage", out var usageEl))
        {
            prompt = TryReadUsageToken(usageEl, "prompt_tokens") ?? TryReadUsageToken(usageEl, "input_tokens");
            completion = TryReadUsageToken(usageEl, "completion_tokens") ?? TryReadUsageToken(usageEl, "output_tokens");
            total = TryReadUsageToken(usageEl, "total_tokens");
        }

        return new ModelTurnResult(content, model, prompt, completion, total, raw);
    }

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendWithPromptFallbackAsync(
        Uri endpoint,
        ModelTurnRequest request,
        SystemPromptDeliveryMode initialMode,
        CancellationToken ct)
    {
        var (statusCode, raw) = await SendWithResponseFormatFallbackAsync(endpoint, request, initialMode, ct);
        if (!IsSuccessStatusCode(statusCode) &&
            ShouldFallbackSystemPromptToUserMessage(initialMode) &&
            IsSystemPromptRejected(statusCode, raw))
        {
            (statusCode, raw) = await SendWithResponseFormatFallbackAsync(endpoint, request, SystemPromptDeliveryMode.UserMessage, ct);
        }

        return (statusCode, raw);
    }

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendWithResponseFormatFallbackAsync(
        Uri endpoint,
        ModelTurnRequest request,
        SystemPromptDeliveryMode mode,
        CancellationToken ct)
    {
        var useResponseFormat = Config.Api.PreferJsonResponseFormat;
        var (statusCode, raw) = await SendCustomRequestAsync(endpoint, request, useResponseFormat, mode, ct);
        if (!IsSuccessStatusCode(statusCode) &&
            useResponseFormat &&
            Config.Api.ResponseFormatFallbackWithoutJson &&
            IsResponseFormatRejected(statusCode, raw))
        {
            (statusCode, raw) = await SendCustomRequestAsync(endpoint, request, false, mode, ct);
        }

        return (statusCode, raw);
    }

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendCustomRequestAsync(
        Uri endpoint,
        ModelTurnRequest request,
        bool useResponseFormat,
        SystemPromptDeliveryMode mode,
        CancellationToken ct)
    {
        var systemPrompt = mode == SystemPromptDeliveryMode.UserMessage ? null : request.SystemPrompt;
        var messages = BuildMessages(request, mode);
        var payload = new
        {
            model = request.Model,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            stream = false,
            system_prompt = systemPrompt,
            messages = messages,
            metadata = request.Metadata,
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

    private static object[] BuildMessages(ModelTurnRequest request, SystemPromptDeliveryMode mode)
    {
        var list = new List<object>();
        if (mode == SystemPromptDeliveryMode.UserMessage || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "user", content = BuildSystemPromptAsUserMessage(request.SystemPrompt) });
        }

        list.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));
        return list.ToArray();
    }

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 200 && code <= 299;
    }

    private static string ExtractContent(JsonElement root)
    {
        if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.String)
        {
            return responseEl.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.String)
        {
            return outputEl.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            return textEl.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("choices", out var choicesEl) &&
            choicesEl.ValueKind == JsonValueKind.Array &&
            choicesEl.GetArrayLength() > 0)
        {
            var first = choicesEl[0];
            if (first.TryGetProperty("message", out var messageEl) &&
                messageEl.TryGetProperty("content", out var contentEl) &&
                contentEl.ValueKind == JsonValueKind.String)
            {
                return contentEl.GetString() ?? string.Empty;
            }

            if (first.TryGetProperty("text", out var choiceTextEl) && choiceTextEl.ValueKind == JsonValueKind.String)
            {
                return choiceTextEl.GetString() ?? string.Empty;
            }
        }

        return root.ToString();
    }

    private static string ExtractModel(JsonElement root, string fallback)
    {
        if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            return modelEl.GetString() ?? fallback;
        }

        return fallback;
    }
}
