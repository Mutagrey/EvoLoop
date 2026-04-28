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
            var defaultConfig = new AgentConfig();
            var defaultJson = JsonSerializer.Serialize(defaultConfig, JsonOptions);
            File.WriteAllText(path, defaultJson);
            return defaultConfig;
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
}
