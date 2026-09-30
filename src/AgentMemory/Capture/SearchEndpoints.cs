using System.Text.Json.Serialization;
using AgentMemory.Retrieval;
using AgentMemory.Storage;

namespace AgentMemory.Capture;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/search");

        // ── Semantic search ──────────────────────────────────────────
        // Called by the hook for pre-prompt context injection, or by Claude Code
        // on-demand via the MCP tool.
        group.MapPost("/", async (
            SearchRequest request,
            IRetrievalService retrieval,
            ILogger<SearchRequest> logger) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest(new { error = "query is required" });
            }

            var results = await retrieval.SearchAsync(
                request.Query,
                limit: request.Limit ?? 5,
                minSimilarity: request.MinSimilarity ?? 0.7,
                category: request.Category,
                ct: CancellationToken.None);

            logger.LogInformation(
                "Search for query (len={QueryLen}) returned {Count} results",
                request.Query.Length, results.Count);

            return Results.Ok(new SearchResponse
            {
                Results = results.Select(r => new SearchResultItem
                {
                    Id = r.Id,
                    Category = r.Category,
                    Score = r.Score,
                    Content = r.Content,
                    CreatedAt = r.CreatedAt,
                    Similarity = r.Similarity
                }).ToList()
            });
        })
        .WithName("SearchMemories");

        // ── Context injection helper ─────────────────────────────────
        // Called by the UserPromptSubmit hook: returns the top memories that
        // should be injected as context before the prompt, formatted as a text
        // block the hook can prepend.
        group.MapPost("/context", async (
            SearchRequest request,
            IRetrievalService retrieval,
            ILogger<SearchRequest> logger) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.Ok(new ContextResponse { Context = null });
            }

            var results = await retrieval.SearchAsync(
                request.Query,
                limit: request.Limit ?? 5,
                minSimilarity: request.MinSimilarity ?? 0.75,
                ct: CancellationToken.None);

            if (results.Count == 0)
            {
                return Results.Ok(new ContextResponse { Context = null });
            }

            var context = BuildContextBlock(results);
            logger.LogInformation(
                "Context injection for query (len={QueryLen}): {Count} memories",
                request.Query.Length, results.Count);

            return Results.Ok(new ContextResponse { Context = context });
        })
        .WithName("SearchContext");
    }

    private static string BuildContextBlock(IReadOnlyList<MemorySearchResult> results)
    {
        var lines = new List<string>
        {
            "<relevant_memories from=\"agent-memory\">"
        };

        foreach (var r in results)
        {
            lines.Add($"  <memory id=\"{r.Id}\" category=\"{r.Category}\" relevance=\"{r.Similarity:F2}\">");
            lines.Add($"    {r.Content}");
            lines.Add("  </memory>");
        }

        lines.Add("</relevant_memories>");
        return string.Join("\n", lines);
    }
}

// ── Request / Response types ─────────────────────────────────────

public record SearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int? Limit,
    [property: JsonPropertyName("min_similarity")] double? MinSimilarity,
    [property: JsonPropertyName("category")] string? Category
);

public class SearchResponse
{
    [JsonPropertyName("results")]
    public List<SearchResultItem> Results { get; set; } = new();
}

public class SearchResultItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
}

public class ContextResponse
{
    [JsonPropertyName("context")]
    public string? Context { get; set; }
}