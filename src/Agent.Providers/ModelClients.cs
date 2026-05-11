using System.Net.Http.Headers;
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
