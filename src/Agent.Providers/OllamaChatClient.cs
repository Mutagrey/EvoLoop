using System.Net;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

internal sealed class OllamaChatClient : ModelClientBase, IModelAdapter
{
    public OllamaChatClient(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
        : base(httpClient, config, profile)
    {
    }

    public ModelAdapterCapabilities AdapterCapabilities => ModelAdapterCapabilities.JsonOnly;

    public async Task<ModelAdapterTurnResult> CompleteTurnAsync(ModelAdapterTurnRequest request, CancellationToken ct)
        => await CompleteJsonReActFallbackAsync(request, ct);

    protected override async Task<ModelTurnResult> CompleteCoreAsync(ModelTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.CustomPath);
        EnsureEndpointAllowed(endpoint);
        var initialMode = ResolveSystemPromptMode();
        var (statusCode, raw) = await SendWithPromptAndResponseFormatFallbackAsync(
            initialMode,
            (useResponseFormat, mode, token) => SendOllamaRequestAsync(endpoint, request, useResponseFormat, mode, token),
            ct);

        if (!IsSuccessStatusCode(statusCode))
        {
            throw new InvalidOperationException(BuildHttpError("Ollama chat", statusCode, endpoint, raw));
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var content = ExtractContent(root);
        var model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString() ?? request.Model
            : request.Model;
        var prompt = TryReadUsageToken(root, "prompt_eval_count");
        var completion = TryReadUsageToken(root, "eval_count");
        int? total = prompt.HasValue || completion.HasValue
            ? prompt.GetValueOrDefault() + completion.GetValueOrDefault()
            : null;

        return new ModelTurnResult(content, model, prompt, completion, total, raw);
    }

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendOllamaRequestAsync(
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
            think = false,
            messages = BuildMessages(request, mode),
            format = useResponseFormat ? "json" : null,
            options = new
            {
                temperature = request.Temperature,
                num_predict = request.MaxTokens
            }
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
        if (mode == SystemPromptDeliveryMode.System || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "system", content = request.SystemPrompt });
        }

        if (mode == SystemPromptDeliveryMode.UserMessage || mode == SystemPromptDeliveryMode.Both)
        {
            list.Add(new { role = "user", content = BuildSystemPromptAsUserMessage(request.SystemPrompt) });
        }

        list.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));
        return list.ToArray();
    }

    private static string ExtractContent(JsonElement root)
    {
        if (root.TryGetProperty("message", out var messageEl) &&
            messageEl.ValueKind == JsonValueKind.Object &&
            messageEl.TryGetProperty("content", out var contentEl) &&
            contentEl.ValueKind == JsonValueKind.String)
        {
            return contentEl.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.String)
        {
            return responseEl.GetString() ?? string.Empty;
        }

        return root.ToString();
    }
}
