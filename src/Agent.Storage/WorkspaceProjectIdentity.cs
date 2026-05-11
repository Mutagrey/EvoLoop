using System.Text;
using System.Text.Json;

namespace Agent.Storage;

public sealed partial class WorkspaceMemoryStore
{
    private string ResolveOrCreateProjectIdentity(string workspaceRoot)
    {
        var identityPath = Path.Combine(workspaceRoot, ".evoloop", "project.identity.json");
        var identityDir = Path.GetDirectoryName(identityPath);
        if (!string.IsNullOrWhiteSpace(identityDir))
        {
            Directory.CreateDirectory(identityDir);
        }

        ProjectIdentityDocument? existing = null;
        if (File.Exists(identityPath))
        {
            try
            {
                existing = JsonSerializer.Deserialize<ProjectIdentityDocument>(File.ReadAllText(identityPath), JsonOptions);
            }
            catch
            {
                existing = null;
            }
        }

        var projectId = existing?.ProjectId?.Trim();
        var createdAtUtc = existing?.CreatedAtUtc;
        var firstWorkspaceRoot = existing?.FirstWorkspaceRoot;
        var derivation = existing?.Derivation;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            var origin = TryReadGitOriginUrl(workspaceRoot);
            if (!string.IsNullOrWhiteSpace(origin))
            {
                projectId = "git-" + ComputeSha256Hex(origin.Trim().ToLowerInvariant())[..24];
                derivation = "git_origin";
            }
            else
            {
                projectId = "local-" + Guid.NewGuid().ToString("n");
                derivation = "local_guid";
            }
        }

        createdAtUtc ??= DateTimeOffset.UtcNow.ToString("O");
        firstWorkspaceRoot ??= workspaceRoot;
        derivation ??= "local_guid";

        var normalizedCurrentRoot = Path.GetFullPath(workspaceRoot);
        var needsSave = existing is null ||
                        !string.Equals(existing.ProjectId, projectId, StringComparison.Ordinal) ||
                        !string.Equals(existing.LastWorkspaceRoot, normalizedCurrentRoot, StringComparison.Ordinal) ||
                        !string.Equals(existing.FirstWorkspaceRoot, firstWorkspaceRoot, StringComparison.Ordinal) ||
                        !string.Equals(existing.Derivation, derivation, StringComparison.Ordinal);

        if (needsSave)
        {
            var doc = new ProjectIdentityDocument(
                projectId,
                createdAtUtc,
                firstWorkspaceRoot,
                normalizedCurrentRoot,
                derivation);
            try
            {
                File.WriteAllText(identityPath, JsonSerializer.Serialize(doc, JsonOptions), Encoding.UTF8);
            }
            catch
            {
                // Identity persistence is best-effort.
            }
        }

        return projectId;
    }

    private static string? TryResolvePortableRunsPath(string projectId)
    {
        var dataRoot = GetUserDataRoot();
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return null;
        }

        try
        {
            var memoryRoot = Path.Combine(dataRoot, "memory");
            Directory.CreateDirectory(memoryRoot);
            return Path.Combine(memoryRoot, $"{projectId}.jsonl");
        }
        catch
        {
            return null;
        }
    }

    private static string? GetUserDataRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".evoloop");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "EvoLoop");
        }

        return null;
    }

    private static string? TryReadGitOriginUrl(string workspaceRoot)
    {
        var configPath = TryResolveGitConfigPath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(configPath);
            return ParseGitOriginUrl(lines);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveGitConfigPath(string workspaceRoot)
    {
        var dotGit = Path.Combine(workspaceRoot, ".git");
        if (Directory.Exists(dotGit))
        {
            return Path.Combine(dotGit, "config");
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        try
        {
            var line = File.ReadLines(dotGit).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var gitDirRaw = trimmed["gitdir:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(gitDirRaw))
            {
                return null;
            }

            var gitDir = Path.IsPathRooted(gitDirRaw)
                ? gitDirRaw
                : Path.GetFullPath(Path.Combine(workspaceRoot, gitDirRaw));
            return Path.Combine(gitDir, "config");
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseGitOriginUrl(IEnumerable<string> lines)
    {
        var inOrigin = false;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin)
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            if (!key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(equalsIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

}
