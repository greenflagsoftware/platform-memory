using AgentMemory.Retrieval;
using ModelContextProtocol.Server;
using System.Text.Json.Serialization;

namespace AgentMemory.Capture;

/// <summary>
/// MCP server tools exposed over the ModelContextProtocol HTTP/SSE transport.
/// Registered via MapMcp("/mcp") in Program.cs and auto-discovered by Claude Code
/// via `claude mcp add --transport http http://localhost:5098/mcp`.
///
/// Only agent-facing tools live here; capture, admin, and search/context endpoints
/// stay as plain HTTP minimal APIs.
/// </summary>
public class McpTools
{
    private readonly IRetrievalService _retrieval;

    public McpTools(IRetrievalService retrieval)
    {
        _retrieval = retrieval;
    }

/// <summary>
/// Search past memories semantically. Returns memories whose content is similar
/// to the query, with similarity scores. Call this before making a significant
/// decision, before asking about project conventions, or when you suspect relevant
/// context might exist from a past session.
/// </summary>
[McpServerTool(Name = "search_memories", ReadOnly = true)]
public async Task<McpSearchMemoriesResult> SearchMemoriesAsync(
    string query,
    int? limit = null,
    double? min_similarity = null,
    string? category = null,
    CancellationToken ct = default)
    {
        var results = await _retrieval.SearchAsync(
            query,
            limit: limit ?? 5,
            minSimilarity: min_similarity ?? 0.7,
            category: category,
            ct: ct);

        return new McpSearchMemoriesResult
        {
            Query = query,
            ResultCount = results.Count,
            Results = results.Select(r => new McpSearchResultItem
            {
                Id = r.Id,
                Category = r.Category,
                Score = r.Score,
                Content = r.Content,
                CreatedAt = r.CreatedAt,
                Similarity = r.Similarity
            }).ToList()
        };
    }
}

// ── Result types ────────────────────────────────────────────────

public class McpSearchMemoriesResult
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("result_count")]
    public int ResultCount { get; set; }

    [JsonPropertyName("results")]
    public List<McpSearchResultItem> Results { get; set; } = new();
}

public class McpSearchResultItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
}