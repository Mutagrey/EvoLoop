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
        var key = SearchRanking.BuildRerankCacheKey(task, limited);
        if (_cache.TryGet(key, out var cachedScores) && cachedScores.Count == limited.Count)
        {
            return SearchRanking.MergeScores(limited, cachedScores);
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

            var lexical = SearchRanking.ScoreLexical(query.Query, snippet);
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

        foreach (var file in SafeWorkspaceFileEnumerator.EnumerateFiles(query.WorkspaceRoot, query.IncludeHidden, ct))
        {
            ct.ThrowIfCancellationRequested();

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
                    var lexical = SearchRanking.ScoreLexical(query.Query, lines[i]);
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

        var prompt = SearchRanking.BuildRerankPrompt(task, candidates);
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
        var ranking = SearchRanking.ParseRerankResponse(response.Content, candidates.Count);

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
                semanticScores[i] = SearchRanking.Normalize(candidates[i].LexicalScore);
            }
        }

        return SearchRanking.MergeScores(candidates, semanticScores);
    }
}
