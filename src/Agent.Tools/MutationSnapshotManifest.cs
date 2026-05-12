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

    public static string GetManifestPath(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".evoloop", "storage", "snapshots", "last-mutation.json");

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
}
