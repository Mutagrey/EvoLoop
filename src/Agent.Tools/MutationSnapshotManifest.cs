using System.Text;
using System.Text.Json;

namespace Agent.Tools;

internal sealed record MutationSnapshotManifest(
    string RelativePath,
    bool ExistedBefore,
    bool IsDirectory,
    string SnapshotPath,
    DateTimeOffset CapturedAtUtc);

internal static class MutationSnapshotManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new();

    public static string GetManifestPath(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".evoloop", "storage", "snapshots", "last-mutation.json");

    public static string GetHistoryPath(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".evoloop", "storage", "snapshots", "mutation-history.jsonl");

    public static async Task<MutationSnapshotManifest?> ReadAsync(string workspaceRoot, CancellationToken ct)
    {
        var manifestPath = GetManifestPath(workspaceRoot);
        return JsonSerializer.Deserialize<MutationSnapshotManifest>(
            await File.ReadAllTextAsync(manifestPath, ct),
            JsonOptions);
    }

    public static Task WriteAsync(string workspaceRoot, MutationSnapshotManifest manifest, CancellationToken ct)
        => File.WriteAllTextAsync(
            GetManifestPath(workspaceRoot),
            JsonSerializer.Serialize(manifest, JsonOptions),
            Encoding.UTF8,
            ct);

    public static Task AppendHistoryAsync(string workspaceRoot, MutationSnapshotManifest manifest, CancellationToken ct)
        => File.AppendAllTextAsync(
            GetHistoryPath(workspaceRoot),
            JsonSerializer.Serialize(manifest, JsonLineOptions) + Environment.NewLine,
            Encoding.UTF8,
            ct);

    public static async Task<IReadOnlyList<MutationSnapshotManifest>> ReadHistoryAsync(string workspaceRoot, CancellationToken ct)
    {
        var historyPath = GetHistoryPath(workspaceRoot);
        if (!File.Exists(historyPath))
        {
            return Array.Empty<MutationSnapshotManifest>();
        }

        var manifests = new List<MutationSnapshotManifest>();
        foreach (var line in await File.ReadAllLinesAsync(historyPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var manifest = JsonSerializer.Deserialize<MutationSnapshotManifest>(line, JsonLineOptions);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests;
    }
}
