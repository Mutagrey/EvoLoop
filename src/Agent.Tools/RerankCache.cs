using System.Text.Json;

namespace Agent.Tools;

internal sealed class RerankCache
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, double[]> _cache = new(StringComparer.Ordinal);
    private bool _loaded;

    public RerankCache(string filePath)
    {
        _filePath = filePath;
    }

    public bool TryGet(string key, out IReadOnlyList<double> scores)
    {
        EnsureLoaded();
        if (_cache.TryGetValue(key, out var value))
        {
            scores = value;
            return true;
        }

        scores = Array.Empty<double>();
        return false;
    }

    public async Task SaveAsync(string key, IReadOnlyList<double> scores, CancellationToken ct)
    {
        EnsureLoaded();
        var cloned = scores.ToArray();
        _cache[key] = cloned;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = JsonSerializer.Serialize(new CacheEntry(key, cloned));

        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_filePath, payload + Environment.NewLine, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_filePath))
        {
            return;
        }

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<CacheEntry>(line);
                if (entry is null || string.IsNullOrWhiteSpace(entry.Key) || entry.Scores is null)
                {
                    continue;
                }

                _cache[entry.Key] = entry.Scores;
            }
            catch
            {
                // Ignore corrupted cache lines.
            }
        }
    }

    private sealed record CacheEntry(string Key, double[] Scores);
}
