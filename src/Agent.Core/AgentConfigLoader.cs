using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Core;

public static class AgentConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetDefaultConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Directory.GetCurrentDirectory();
        }

        return Path.Combine(home, ".evoloop-agent", "config.json");
    }

    public static AgentConfig LoadOrCreate(string? customPath = null)
    {
        var path = customPath ?? GetDefaultConfigPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, CreateDefaultConfigJson());
            return new AgentConfig();
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions);
        return config ?? new AgentConfig();
    }

    public static void Save(AgentConfig config, string? customPath = null)
    {
        var path = customPath ?? GetDefaultConfigPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static string CreateDefaultConfigJson()
    {
        var defaults = new AgentConfig();
        var reasoning = defaults.Models["reasoning"];
        var minimal = new Dictionary<string, object?>
        {
            ["api"] = new Dictionary<string, object?>
            {
                ["baseUrl"] = defaults.Api.BaseUrl,
                ["openAiCompatiblePath"] = defaults.Api.OpenAiCompatiblePath,
                ["customPath"] = defaults.Api.CustomPath,
                ["apiKeyEnvVar"] = defaults.Api.ApiKeyEnvVar,
                ["timeoutSeconds"] = defaults.Api.TimeoutSeconds
            },
            ["models"] = new Dictionary<string, object?>
            {
                ["reasoning"] = new Dictionary<string, object?>
                {
                    ["provider"] = reasoning.Provider,
                    ["model"] = reasoning.Model,
                    ["temperature"] = reasoning.Temperature,
                    ["maxTokens"] = reasoning.MaxTokens,
                    ["toolCallingMode"] = reasoning.ToolCallingMode
                }
            },
            ["safety"] = new Dictionary<string, object?>
            {
                ["requireApprovalForWrites"] = defaults.Safety.RequireApprovalForWrites,
                ["requireApprovalForCommits"] = defaults.Safety.RequireApprovalForCommits,
                ["requireApprovalForRiskyShell"] = defaults.Safety.RequireApprovalForRiskyShell,
                ["denyOutsideWorkspace"] = defaults.Safety.DenyOutsideWorkspace,
                ["offlineStrictMode"] = defaults.Safety.OfflineStrictMode,
                ["defaultApprovalMode"] = defaults.Safety.DefaultApprovalMode,
                ["allowedNetworkHosts"] = defaults.Safety.AllowedNetworkHosts
            },
            ["ui"] = new Dictionary<string, object?>
            {
                ["useColor"] = defaults.Ui.UseColor,
                ["compactMode"] = defaults.Ui.CompactMode
            }
        };

        return JsonSerializer.Serialize(minimal, JsonOptions);
    }
}
