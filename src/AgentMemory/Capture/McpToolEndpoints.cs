using AgentMemory.Retrieval;
using AgentMemory.Storage;

namespace AgentMemory.Capture;

/// <summary>
/// MCP-style tool endpoint for on-demand memory search.
/// Claude Code can call this as a tool via its MCP transport (the hook-driven agent
/// discovers it via the system prompt or project instructions).
///
/// This is a REST endpoint, not a JSON-RPC MCP tool endpoint, but it serves the same
/// purpose: the agent sends a semantic query and gets back relevant memories.
///
/// Endpoint: POST /tools/search-memories
/// </summary>
public static class McpToolEndpoints
{
    public static void MapMcpToolEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/tools");

        group.MapPost("/search-memories", async (
            McpSearchRequest request,
            IRetrievalService retrieval,
            ILogger<McpSearchRequest> logger) =>
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
                "MCP tool search for '{Query}' returned {Count} results",
                request.Query, results.Count);

            return Results.Ok(new
            {
                tool = "search_memories",
                query = request.Query,
                result_count = results.Count,
                results = results.Select(r => new
                {
                    r.Id,
                    r.Category,
                    r.Score,
                    r.Content,
                    r.CreatedAt,
                    r.Similarity
                })
            });
        })
        .WithName("ToolSearchMemories");
    }
}

// ── Request type (snake_case for JSON from hooks/agent) ─────────
public record McpSearchRequest(
    string Query,
    int? Limit,
    double? MinSimilarity,
    string? Category
);