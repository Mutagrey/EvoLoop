using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

public sealed class ModelClientRouter : IModelClientRouter, IDisposable
{
    private readonly AgentConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, IModelClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    public ModelClientRouter(AgentConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _config.Api.TimeoutSeconds))
        };

        ConfigureHeaders(_httpClient, _config.Api);
    }

    public IModelClient GetClient(string profileName)
    {
        if (_clientCache.TryGetValue(profileName, out var cached))
        {
            return cached;
        }

        if (!_config.Models.TryGetValue(profileName, out var profile))
        {
            throw new InvalidOperationException($"Model profile '{profileName}' not found in config.");
        }

        IModelClient client = profile.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiCompatibleClient(_httpClient, _config, profile)
            : new CustomGatewayClient(_httpClient, _config, profile);

        _clientCache[profileName] = client;
        return client;
    }

    public string ResolveModelName(string profileName)
    {
        if (_config.Models.TryGetValue(profileName, out var profile))
        {
            return profile.Model;
        }

        throw new InvalidOperationException($"Model profile '{profileName}' not found in config.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static void ConfigureHeaders(HttpClient client, ApiConfig config)
    {
        var apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = config.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        foreach (var pair in config.Headers)
        {
            if (!client.DefaultRequestHeaders.Contains(pair.Key))
            {
                client.DefaultRequestHeaders.Add(pair.Key, pair.Value);
            }
        }
    }
}

internal abstract class ModelClientBase : IModelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    protected readonly HttpClient HttpClient;
    protected readonly AgentConfig Config;
    protected readonly ModelProfileConfig Profile;

    protected ModelClientBase(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
    {
        HttpClient = httpClient;
        Config = config;
        Profile = profile;
    }

    public ModelCapabilities Capabilities => new(SupportsStreaming: false, SupportsEmbeddings: false);

    public async Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct)
    {
        var attempts = 0;
        Exception? lastError = null;

        while (attempts < 3)
        {
            attempts++;
            try
            {
                return await CompleteCoreAsync(request, ct);
            }
            catch (Exception ex) when (attempts < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempts * attempts), ct);
            }
        }

        throw new InvalidOperationException($"Model request failed after retries: {lastError?.Message}", lastError);
    }

    protected abstract Task<ModelTurnResult> CompleteCoreAsync(ModelTurnRequest request, CancellationToken ct);

    protected static StringContent JsonBody(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    protected static Uri BuildEndpoint(string baseUrl, string pathOrAbsoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Api.baseUrl is empty.");
        }

        if (Uri.TryCreate(pathOrAbsoluteUrl, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"Api.baseUrl is not a valid absolute URI: '{baseUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(pathOrAbsoluteUrl))
        {
            return baseUri;
        }

        if (pathOrAbsoluteUrl.StartsWith("/", StringComparison.Ordinal))
        {
            return new Uri($"{baseUri.Scheme}://{baseUri.Authority}{pathOrAbsoluteUrl}");
        }

        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");

        return new Uri(normalizedBase, pathOrAbsoluteUrl);
    }

    protected void EnsureEndpointAllowed(Uri endpoint)
    {
        if (!Config.Safety.OfflineStrictMode)
        {
            return;
        }

        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Uri.TryCreate(Config.Api.BaseUrl, UriKind.Absolute, out var baseUri) && !string.IsNullOrWhiteSpace(baseUri.Host))
        {
            allowedHosts.Add(baseUri.Host);
        }

        foreach (var host in Config.Safety.AllowedNetworkHosts)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                allowedHosts.Add(host.Trim());
            }
        }

        if (!allowedHosts.Contains(endpoint.Host))
        {
            throw new InvalidOperationException(
                $"Offline strict mode blocked model request to host '{endpoint.Host}'. Allowed hosts: {string.Join(", ", allowedHosts)}");
        }
    }

    protected static int? TryReadUsageToken(JsonElement usageEl, string field)
    {
        if (usageEl.ValueKind == JsonValueKind.Object &&
            usageEl.TryGetProperty(field, out var tokenEl) &&
            tokenEl.ValueKind == JsonValueKind.Number &&
            tokenEl.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    protected static string BuildHttpError(string providerLabel, HttpStatusCode statusCode, Uri endpoint, string raw)
    {
        var hint = statusCode == HttpStatusCode.NotFound
            ? " Hint: check Api.baseUrl and endpoint path. If baseUrl has a path prefix (e.g. /v1), use relative path without leading slash."
            : string.Empty;

        return $"{providerLabel} request failed ({(int)statusCode}) endpoint='{endpoint}'.{hint} Response: {raw}";
    }

    protected static bool IsResponseFormatRejected(HttpStatusCode statusCode, string raw)
    {
        if (statusCode != HttpStatusCode.BadRequest && (int)statusCode != 422)
        {
            return false;
        }

        return raw.Contains("response_format", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("json_schema", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("json_object", StringComparison.OrdinalIgnoreCase);
    }

    protected bool ShouldFallbackSystemPromptToUserMessage(SystemPromptDeliveryMode mode)
    {
        return mode != SystemPromptDeliveryMode.UserMessage && Config.Api.SystemPromptFallbackToUserMessage;
    }

    protected SystemPromptDeliveryMode ResolveSystemPromptMode()
    {
        var raw = Config.Api.SystemPromptMode?.Trim().ToLowerInvariant();
        return raw switch
        {
            "user" => SystemPromptDeliveryMode.UserMessage,
            "message" => SystemPromptDeliveryMode.UserMessage,
            "messages" => SystemPromptDeliveryMode.UserMessage,
            "both" => SystemPromptDeliveryMode.Both,
            _ => SystemPromptDeliveryMode.System
        };
    }

    protected static bool IsSystemPromptRejected(HttpStatusCode statusCode, string raw)
    {
        if (statusCode != HttpStatusCode.BadRequest && (int)statusCode != 422)
        {
            return false;
        }

        if (raw.Contains("system_prompt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (raw.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
            raw.Contains("system", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (raw.Contains("role", StringComparison.OrdinalIgnoreCase) &&
            raw.Contains("system", StringComparison.OrdinalIgnoreCase) &&
            raw.Contains("must", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return raw.Contains("unknown field", StringComparison.OrdinalIgnoreCase) &&
               raw.Contains("system", StringComparison.OrdinalIgnoreCase);
    }

    protected static string BuildSystemPromptAsUserMessage(string systemPrompt)
    {
        return "SYSTEM INSTRUCTIONS (obey as highest-priority policy):\n" +
               systemPrompt +
               "\n\nContinue with the normal conversation and tool-decision task.";
    }
}

internal enum SystemPromptDeliveryMode
{
    System,
    UserMessage,
    Both
}

internal sealed class OpenAiCompatibleClient : ModelClientBase
{
    public OpenAiCompatibleClient(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
        : base(httpClient, config, profile)
    {
    }

    protected override async Task<ModelTurnResult> CompleteCoreAsync(ModelTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.OpenAiCompatiblePath);
        EnsureEndpointAllowed(endpoint);
        var initialMode = ResolveSystemPromptMode();
        var (statusCode, raw) = await SendWithPromptFallbackAsync(endpoint, request, initialMode, ct);

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
        var (statusCode, raw) = await SendOpenAiRequestAsync(endpoint, request, useResponseFormat, mode, ct);
        if (!IsSuccessStatusCode(statusCode) &&
            useResponseFormat &&
            Config.Api.ResponseFormatFallbackWithoutJson &&
            IsResponseFormatRejected(statusCode, raw))
        {
            (statusCode, raw) = await SendOpenAiRequestAsync(endpoint, request, false, mode, ct);
        }

        return (statusCode, raw);
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

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 200 && code <= 299;
    }
}

internal sealed class CustomGatewayClient : ModelClientBase
{
    public CustomGatewayClient(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
        : base(httpClient, config, profile)
    {
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
