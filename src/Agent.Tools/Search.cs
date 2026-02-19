using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Core;

namespace Agent.Tools;

public sealed class HybridSearchService : ISearchService
{
    private static readonly Regex RipGrepLineRegex = new(@"^(.*?):(\d+):(.*)$", RegexOptions.Compiled);
    private readonly IModelClientRouter _modelRouter;
    private readonly AgentConfig _config;
    private readonly RerankCache _cache;

    public HybridSearchService(IModelClientRouter modelRouter, AgentConfig config, string workspaceRoot)
    {
        _modelRouter = modelRouter;
        _config = config;
        _cache = new RerankCache(Path.Combine(workspaceRoot, ".evoloop", "storage", "rerank-cache.jsonl"));
    }

    public async Task<IReadOnlyList<SearchHit>> LexicalAsync(SearchQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return Array.Empty<SearchHit>();
        }

        if (ProcessRunner.CommandExists("rg"))
        {
            var fromRg = await SearchWithRipGrepAsync(query, ct);
            if (fromRg.Count > 0)
            {
                return fromRg;
            }
        }

        return await SearchWithFallbackScannerAsync(query, ct);
    }

    public async Task<IReadOnlyList<SearchHit>> RerankAsync(string task, IReadOnlyList<SearchHit> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var limited = candidates.Take(Math.Min(candidates.Count, _config.Runtime.RerankCandidateLimit)).ToList();
        var key = BuildRerankCacheKey(task, limited);
        if (_cache.TryGet(key, out var cachedScores) && cachedScores.Count == limited.Count)
        {
            return MergeScores(limited, cachedScores);
        }

        try
        {
            var reranked = await RerankWithModelAsync(task, limited, ct);
            var scores = reranked.Select(x => x.SemanticScore).ToArray();
            await _cache.SaveAsync(key, scores, ct);
            return reranked;
        }
        catch
        {
            return limited
                .Select(hit => hit with { SemanticScore = hit.LexicalScore, FinalScore = hit.LexicalScore })
                .OrderByDescending(hit => hit.FinalScore)
                .ToList();
        }
    }

    private async Task<IReadOnlyList<SearchHit>> SearchWithRipGrepAsync(SearchQuery query, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--line-number",
            "--no-heading",
            "--color",
            "never",
            query.CaseSensitive ? "--case-sensitive" : "--smart-case"
        };

        if (query.IncludeHidden)
        {
            args.Add("--hidden");
        }

        if (!string.IsNullOrWhiteSpace(query.Glob))
        {
            args.Add("--glob");
            args.Add(query.Glob!);
        }

        args.Add(query.Query);
        args.Add(".");

        var result = await ProcessRunner.RunAsync("rg", args, query.WorkspaceRoot, ct);
        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
        {
            return Array.Empty<SearchHit>();
        }

        var hits = new List<SearchHit>();
        var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (!TryParseRipGrepLine(line, out var path, out var lineNo, out var snippet))
            {
                continue;
            }

            var lexical = ScoreLexical(query.Query, snippet);
            hits.Add(new SearchHit(path, lineNo, snippet, lexical, 0, lexical));
            if (hits.Count >= query.MaxResults * 4)
            {
                break;
            }
        }

        return hits
            .OrderByDescending(x => x.LexicalScore)
            .Take(query.MaxResults)
            .ToList();
    }

    private static bool TryParseRipGrepLine(string line, out string path, out int lineNo, out string snippet)
    {
        path = string.Empty;
        lineNo = 0;
        snippet = string.Empty;

        var match = RipGrepLineRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        path = match.Groups[1].Value;
        snippet = match.Groups[3].Value;
        return int.TryParse(match.Groups[2].Value, out lineNo);
    }

    private static async Task<IReadOnlyList<SearchHit>> SearchWithFallbackScannerAsync(SearchQuery query, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        var comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var file in Directory.EnumerateFiles(query.WorkspaceRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (ShouldSkipFile(query.WorkspaceRoot, file))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query.Glob) && !Path.GetFileName(file).Contains(query.Glob!.Trim('*'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, ct);
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query.Query, comparison))
                {
                    var lexical = ScoreLexical(query.Query, lines[i]);
                    hits.Add(new SearchHit(Path.GetRelativePath(query.WorkspaceRoot, file), i + 1, lines[i], lexical, 0, lexical));
                }
            }
        }

        return hits
            .OrderByDescending(h => h.LexicalScore)
            .Take(query.MaxResults)
            .ToList();
    }

    private async Task<IReadOnlyList<SearchHit>> RerankWithModelAsync(string task, List<SearchHit> candidates, CancellationToken ct)
    {
        var profileName = _config.Models.ContainsKey("reasoning") ? "reasoning" : _config.Models.Keys.First();
        var client = _modelRouter.GetClient(profileName);
        var modelName = _modelRouter.ResolveModelName(profileName);

        var prompt = BuildRerankPrompt(task, candidates);
        var request = new ModelTurnRequest(
            profileName,
            modelName,
            "You are a ranking model. Return strict JSON only.",
            new[]
            {
                new ModelMessage("user", prompt)
            },
            0,
            500);

        var response = await client.CompleteAsync(request, ct);
        var ranking = ParseRerankResponse(response.Content, candidates.Count);

        if (ranking.Count == 0)
        {
            return candidates;
        }

        var semanticScores = new double[candidates.Count];
        foreach (var item in ranking)
        {
            if (item.Index >= 0 && item.Index < semanticScores.Length)
            {
                semanticScores[item.Index] = Math.Clamp(item.Score, 0, 1);
            }
        }

        for (var i = 0; i < semanticScores.Length; i++)
        {
            if (semanticScores[i] <= 0)
            {
                semanticScores[i] = Normalize(candidates[i].LexicalScore);
            }
        }

        return MergeScores(candidates, semanticScores);
    }

    private static IReadOnlyList<SearchHit> MergeScores(IReadOnlyList<SearchHit> candidates, IReadOnlyList<double> semanticScores)
    {
        var result = new List<SearchHit>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var lexical = Normalize(candidates[i].LexicalScore);
            var semantic = Normalize(semanticScores[i]);
            var finalScore = (lexical * 0.4) + (semantic * 0.6);
            result.Add(candidates[i] with { SemanticScore = semantic, FinalScore = finalScore });
        }

        return result.OrderByDescending(x => x.FinalScore).ToList();
    }

    private static string BuildRerankPrompt(string task, IReadOnlyList<SearchHit> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Task:");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("Rank the following code snippets by relevance to the task.");
        sb.AppendLine("Return strict JSON with schema: {\"ranking\":[{\"index\":0,\"score\":0.0}]}");
        sb.AppendLine("Score range: 0.0 to 1.0");

        for (var i = 0; i < candidates.Count; i++)
        {
            var snippet = candidates[i].Snippet;
            if (snippet.Length > 240)
            {
                snippet = snippet[..240];
            }

            sb.AppendLine($"[{i}] {candidates[i].FilePath}:{candidates[i].Line}");
            sb.AppendLine(snippet);
        }

        return sb.ToString();
    }

    private static List<(int Index, double Score)> ParseRerankResponse(string content, int count)
    {
        var list = new List<(int Index, double Score)>();

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ranking", out var ranking) || ranking.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in ranking.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var indexEl) || !indexEl.TryGetInt32(out var index))
                {
                    continue;
                }

                if (!item.TryGetProperty("score", out var scoreEl) || !scoreEl.TryGetDouble(out var score))
                {
                    continue;
                }

                if (index < 0 || index >= count)
                {
                    continue;
                }

                list.Add((index, score));
            }
        }
        catch
        {
            // ignore parse issues and fallback to lexical rank
        }

        return list;
    }

    private static bool ShouldSkipFile(string workspaceRoot, string filePath)
    {
        var rel = Path.GetRelativePath(workspaceRoot, filePath);
        if (rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(filePath);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".zip", ".exe", ".dll", ".so", ".dylib", ".pdf"
        };

        return blocked.Contains(extension);
    }

    private static double ScoreLexical(string query, string text)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return 0;
        }

        var score = 0.0;
        foreach (var word in words)
        {
            score += CountOccurrences(text, word) * 1.5;
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        score += Math.Max(0, 1.0 - (text.Length / 400.0));
        return score;
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static double Normalize(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value > 1 ? 1 : value;
    }

    private static string BuildRerankCacheKey(string task, IReadOnlyList<SearchHit> candidates)
    {
        var sb = new StringBuilder();
        sb.Append(task);
        foreach (var c in candidates)
        {
            sb.Append('|').Append(c.FilePath).Append(':').Append(c.Line).Append(':').Append(c.Snippet);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}

public sealed class SearchLexicalTool : ITool
{
    public string Name => "search_lexical";

    public ToolSchema Schema => new(
        "Search workspace using lexical matching.",
        new[] { "query" },
        new Dictionary<string, string>
        {
            ["query"] = "Text to search.",
            ["max_results"] = "Maximum number of hits.",
            ["glob"] = "Optional glob pattern.",
            ["case_sensitive"] = "Case sensitive search.",
            ["include_hidden"] = "Include hidden files."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var query = ToolArgumentReader.GetString(call.Arguments, "query") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult(false, "Missing required argument: query");
        }

        var request = new SearchQuery(
            context.WorkspaceRoot,
            query,
            Math.Clamp(ToolArgumentReader.GetInt32(call.Arguments, "max_results", context.Config.Runtime.LexicalSearchDefaultMaxResults), 1, 200),
            ToolArgumentReader.GetString(call.Arguments, "glob"),
            ToolArgumentReader.GetBool(call.Arguments, "case_sensitive", false),
            ToolArgumentReader.GetBool(call.Arguments, "include_hidden", false));

        var hits = await context.SearchService.LexicalAsync(request, ct);
        return new ToolResult(true, $"Lexical search returned {hits.Count} hits.", FormatHits(hits));
    }

    private static string FormatHits(IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            sb.AppendLine($"{hit.FilePath}:{hit.Line} [lex={hit.LexicalScore:F3}] {hit.Snippet}");
        }

        return sb.ToString();
    }
}

public sealed class SearchSemanticTool : ITool
{
    public string Name => "search_semantic";

    public ToolSchema Schema => new(
        "Semantic-like search using lexical retrieval + LLM rerank.",
        new[] { "query" },
        new Dictionary<string, string>
        {
            ["query"] = "Search query.",
            ["task"] = "Task context used for reranking.",
            ["max_results"] = "Maximum final results.",
            ["glob"] = "Optional glob pattern."
        });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var query = ToolArgumentReader.GetString(call.Arguments, "query") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult(false, "Missing required argument: query");
        }

        var task = ToolArgumentReader.GetString(call.Arguments, "task") ?? query;
        var maxResults = Math.Clamp(ToolArgumentReader.GetInt32(call.Arguments, "max_results", 10), 1, 100);
        var glob = ToolArgumentReader.GetString(call.Arguments, "glob");

        var lexical = await context.SearchService.LexicalAsync(new SearchQuery(
            context.WorkspaceRoot,
            query,
            Math.Max(maxResults * 3, 15),
            glob,
            false,
            false), ct);

        var reranked = await context.SearchService.RerankAsync(task, lexical, ct);
        var top = reranked.Take(maxResults).ToList();

        var sb = new StringBuilder();
        foreach (var hit in top)
        {
            sb.AppendLine($"{hit.FilePath}:{hit.Line} [lex={hit.LexicalScore:F3} sem={hit.SemanticScore:F3} final={hit.FinalScore:F3}] {hit.Snippet}");
        }

        return new ToolResult(true, $"Semantic search returned {top.Count} hits.", sb.ToString());
    }
}

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
                // ignore corrupted cache lines
            }
        }
    }

    private sealed record CacheEntry(string Key, double[] Scores);
}
