using System.Text;
using Agent.Core;

namespace Agent.Tools;

public sealed class SearchLexicalTool : ITool
{
    public string Name => "search_lexical";
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Search, false, Array.Empty<string>());

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
        var modeSuffix = context.Capabilities.RipgrepAvailable
            ? "rg-enabled"
            : "fallback scanner (rg unavailable)";
        return new ToolResult(true, $"Lexical search returned {hits.Count} hits via {modeSuffix}.", FormatHits(hits));
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
    public ToolMetadata Metadata => new(ToolRiskLevel.Low, ToolCategory.Search, false, new[] { "model" });

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

        IReadOnlyList<SearchHit> reranked;
        string mode;
        if (!context.Capabilities.CanRunAgentTasks || context.ExecutionMode == AgentExecutionMode.Review)
        {
            reranked = lexical;
            mode = "lexical-only fallback (model rerank unavailable)";
        }
        else
        {
            reranked = await context.SearchService.RerankAsync(task, lexical, ct);
            mode = "lexical retrieval + model rerank";
        }

        var top = reranked.Take(maxResults).ToList();

        var sb = new StringBuilder();
        foreach (var hit in top)
        {
            sb.AppendLine($"{hit.FilePath}:{hit.Line} [lex={hit.LexicalScore:F3} sem={hit.SemanticScore:F3} final={hit.FinalScore:F3}] {hit.Snippet}");
        }

        return new ToolResult(true, $"Semantic search returned {top.Count} hits via {mode}.", sb.ToString());
    }
}
