using AgentMemory.Storage;

namespace AgentMemory.Retrieval;

/// <summary>
/// Orchestrates semantic search: embeds a query, then searches stored memories
/// by cosine similarity. Used by both the search endpoint and pre-prompt context injection.
/// </summary>
public interface IRetrievalService
{
    /// <summary>
    /// Search memories semantically. Returns up to <paramref name="limit"/> results
    /// that score above <paramref name="minSimilarity"/> (cosine similarity, 0-1).
    /// </summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query,
        int limit = 5,
        double minSimilarity = 0.7,
        string? category = null,
        CancellationToken ct = default
    );
}