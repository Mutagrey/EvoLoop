using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Core;

namespace Agent.Tools;

internal static class SearchRanking
{
    public static IReadOnlyList<SearchHit> MergeScores(IReadOnlyList<SearchHit> candidates, IReadOnlyList<double> semanticScores)
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

    public static string BuildRerankPrompt(string task, IReadOnlyList<SearchHit> candidates)
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

    public static List<(int Index, double Score)> ParseRerankResponse(string content, int count)
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
            // Ignore parse issues and fallback to lexical rank.
        }

        return list;
    }

    public static double ScoreLexical(string query, string text)
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

    public static double Normalize(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value > 1 ? 1 : value;
    }

    public static string BuildRerankCacheKey(string task, IReadOnlyList<SearchHit> candidates)
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
}
