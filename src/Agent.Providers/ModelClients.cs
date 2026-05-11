using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Providers;

public sealed class ModelClientRouter : IModelClientRouter, IModelAdapterRouter, IDisposable
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

    public IModelAdapter GetAdapter(string profileName, ToolCallingMode requestedMode)
    {
        var client = GetClient(profileName);
        return client is IModelAdapter adapter ? adapter : new ModelClientBackedAdapter(client);
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

public sealed class DisabledModelClientRouter : IModelClientRouter, IModelAdapterRouter
{
    private readonly IModelClient _client;
    private readonly string _message;

    public DisabledModelClientRouter(string message)
    {
        _message = message;
        _client = new DisabledModelClient(message);
    }

    public IModelClient GetClient(string profileName) => _client;

    public string ResolveModelName(string profileName) => $"disabled:{profileName}";

    public IModelAdapter GetAdapter(string profileName, ToolCallingMode requestedMode)
        => new ModelClientBackedAdapter(_client);
}

internal sealed class DisabledModelClient : IModelClient
{
    private readonly string _message;

    public DisabledModelClient(string message)
    {
        _message = message;
    }

    public ModelCapabilities Capabilities => new(false, false);

    public Task<ModelTurnResult> CompleteAsync(ModelTurnRequest request, CancellationToken ct)
    {
        throw new InvalidOperationException(_message);
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

    private async Task<ModelAdapterTurnResult> CompleteJsonFallbackAsync(ModelAdapterTurnRequest request, CancellationToken ct)
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

    private async Task<ModelAdapterTurnResult> CompleteNativeNonStreamingAsync(ModelAdapterTurnRequest request, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(Config.Api.BaseUrl, Config.Api.OpenAiCompatiblePath);
        EnsureEndpointAllowed(endpoint);
        var initialMode = ResolveSystemPromptMode();
        var (statusCode, raw) = await SendNativeWithPromptFallbackAsync(endpoint, request, initialMode, stream: false, ct);

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

    private async Task<(HttpStatusCode StatusCode, string Raw)> SendNativeWithPromptFallbackAsync(
        Uri endpoint,
        ModelAdapterTurnRequest request,
        SystemPromptDeliveryMode initialMode,
        bool stream,
        CancellationToken ct)
    {
        var (statusCode, raw) = await SendNativeOpenAiRequestAsync(endpoint, request, initialMode, stream, ct);
        if (!IsSuccessStatusCode(statusCode) &&
            ShouldFallbackSystemPromptToUserMessage(initialMode) &&
            IsSystemPromptRejected(statusCode, raw))
        {
            (statusCode, raw) = await SendNativeOpenAiRequestAsync(endpoint, request, SystemPromptDeliveryMode.UserMessage, stream, ct);
        }

        return (statusCode, raw);
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

internal sealed class StreamingToolCallAccumulator
{
    private readonly StringBuilder _arguments = new();

    public StreamingToolCallAccumulator(int index)
    {
        Index = index;
    }

    public int Index { get; }
    public string? Id { get; private set; }
    public string? Name { get; private set; }

    public void ApplyDelta(JsonElement delta)
    {
        if (delta.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            Id = idEl.GetString() ?? Id;
        }

        if (!delta.TryGetProperty("function", out var functionEl) || functionEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (functionEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            Name = nameEl.GetString() ?? Name;
        }

        if (functionEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
        {
            _arguments.Append(argsEl.GetString());
        }
    }

    public bool TryBuild(out ToolCallBlock block, out string error)
    {
        block = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = $"Malformed streaming native tool call at index {Index}: missing tool call id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = $"Malformed streaming native tool call at index {Index}: missing function name.";
            return false;
        }

        var rawArgs = _arguments.Length == 0 ? "{}" : _arguments.ToString();
        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Malformed streaming native tool call at index {Index}: arguments must be a JSON object.";
                return false;
            }

            block = new ToolCallBlock(
                new ToolCallId(Id!),
                new ToolName(Name!),
                doc.RootElement.Clone(),
                "native streaming tool call",
                new Dictionary<string, string> { ["index"] = Index.ToString() });
            return true;
        }
        catch (Exception ex)
        {
            error = $"Malformed streaming native tool call at index {Index}: invalid JSON arguments. {ex.Message}";
            return false;
        }
    }
}

internal sealed class ProbeTool : ITool
{
    public string Name => "evoloop_probe_noop";
    public ToolSchema Schema => new(
        "Safe no-op probe used by EvoLoop to detect native tool-call support.",
        Array.Empty<string>(),
        new Dictionary<string, string> { ["ok"] = "Any boolean marker." });
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Status, false, Array.Empty<string>());

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(true, "probe noop"));
}

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
