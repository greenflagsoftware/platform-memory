using AgentMemory.Retrieval;
using ModelContextProtocol.Server;
using System.ComponentModel;
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
[McpServerToolType]
public class McpTools
{
    private readonly IRetrievalService _retrieval;

    public McpTools(IRetrievalService retrieval)
    {
        _retrieval = retrieval;
    }

    [McpServerTool(Name = "search_memories", ReadOnly = true)]
    [Description(
        "Search past memories from this project's AgentMemory sidecar. Returns memories " +
        "whose content is semantically similar to the query, with similarity scores. Call " +
        "this before making a significant decision, before asking about project " +
        "conventions, or when you suspect relevant context might exist from a past " +
        "session. This is explicitly an on-demand tool — call it when you need it, not on " +
        "every turn (relevant memories are already injected automatically before each " +
        "prompt).")]
    public async Task<McpSearchMemoriesResult> SearchMemoriesAsync(
        [Description("The search query.")] string query,
        [Description("Maximum number of results to return. Defaults to 5.")] int? limit = null,
        [Description("Minimum cosine similarity (0-1) a result must meet. Defaults to 0.7.")] double? min_similarity = null,
        [Description("Optional category filter, e.g. 'decision', 'coding', 'configuration'.")] string? category = null,
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