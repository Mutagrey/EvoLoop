using System.Net;
using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

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

        if (Uri.TryCreate(pathOrAbsoluteUrl, UriKind.Absolute, out var absolute) &&
            !string.IsNullOrWhiteSpace(absolute.Scheme) &&
            (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return absolute;
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

    protected static bool IsSuccessStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 200 && code <= 299;
    }

    protected async Task<(HttpStatusCode StatusCode, string Raw)> SendWithSystemPromptFallbackAsync(
        SystemPromptDeliveryMode initialMode,
        Func<SystemPromptDeliveryMode, CancellationToken, Task<(HttpStatusCode StatusCode, string Raw)>> send,
        CancellationToken ct)
    {
        var (statusCode, raw) = await send(initialMode, ct);
        if (!IsSuccessStatusCode(statusCode) &&
            ShouldFallbackSystemPromptToUserMessage(initialMode) &&
            IsSystemPromptRejected(statusCode, raw))
        {
            (statusCode, raw) = await send(SystemPromptDeliveryMode.UserMessage, ct);
        }

        return (statusCode, raw);
    }

    protected async Task<(HttpStatusCode StatusCode, string Raw)> SendWithResponseFormatFallbackAsync(
        SystemPromptDeliveryMode mode,
        Func<bool, SystemPromptDeliveryMode, CancellationToken, Task<(HttpStatusCode StatusCode, string Raw)>> send,
        CancellationToken ct)
    {
        var useResponseFormat = Config.Api.PreferJsonResponseFormat;
        var (statusCode, raw) = await send(useResponseFormat, mode, ct);
        if (!IsSuccessStatusCode(statusCode) &&
            useResponseFormat &&
            Config.Api.ResponseFormatFallbackWithoutJson &&
            IsResponseFormatRejected(statusCode, raw))
        {
            (statusCode, raw) = await send(false, mode, ct);
        }

        return (statusCode, raw);
    }

    protected Task<(HttpStatusCode StatusCode, string Raw)> SendWithPromptAndResponseFormatFallbackAsync(
        SystemPromptDeliveryMode initialMode,
        Func<bool, SystemPromptDeliveryMode, CancellationToken, Task<(HttpStatusCode StatusCode, string Raw)>> send,
        CancellationToken ct)
    {
        return SendWithSystemPromptFallbackAsync(
            initialMode,
            (mode, token) => SendWithResponseFormatFallbackAsync(mode, send, token),
            ct);
    }

    protected async Task<ModelAdapterTurnResult> CompleteJsonReActFallbackAsync(ModelAdapterTurnRequest request, CancellationToken ct)
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
