using System.Net.Http.Headers;
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
}

internal sealed class OpenAiCompatibleClient : ModelClientBase
{
    public OpenAiCompatibleClient(HttpClient httpClient, AgentConfig config, ModelProfileConfig profile)
        : base(httpClient, config, profile)
    {
    }

    protected override async Task<ModelTurnResult> CompleteCoreAsync(ModelTurnRequest request, CancellationToken ct)
    {
        var endpoint = new Uri(new Uri(Config.Api.BaseUrl), Config.Api.OpenAiCompatiblePath);
        EnsureEndpointAllowed(endpoint);
        var payload = new
        {
            model = request.Model,
            stream = false,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            messages = BuildMessages(request)
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonBody(payload)
        };

        using var response = await HttpClient.SendAsync(httpRequest, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI-compatible request failed ({(int)response.StatusCode}): {raw}");
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

    private static List<object> BuildMessages(ModelTurnRequest request)
    {
        var list = new List<object>
        {
            new { role = "system", content = request.SystemPrompt }
        };

        list.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));
        return list;
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
        var endpoint = new Uri(new Uri(Config.Api.BaseUrl), Config.Api.CustomPath);
        EnsureEndpointAllowed(endpoint);
        var payload = new
        {
            model = request.Model,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            stream = false,
            system_prompt = request.SystemPrompt,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            metadata = request.Metadata
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonBody(payload)
        };

        using var response = await HttpClient.SendAsync(httpRequest, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Custom gateway request failed ({(int)response.StatusCode}): {raw}");
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
