using AgentMemory.Embedding;
using AgentMemory.Storage;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace AgentMemory.Retrieval;

/// <summary>
/// Embeds the query text, then searches the memories table by cosine similarity.
/// Returns empty results gracefully on embedding failure (no throw).
/// </summary>
public class RetrievalService : IRetrievalService
{
    private readonly IEmbeddingService _embedding;
    private readonly IMemoryRepository _memoryRepo;
    private readonly ILogger<RetrievalService> _logger;

    public RetrievalService(
        IEmbeddingService embedding,
        IMemoryRepository memoryRepo,
        ILogger<RetrievalService> logger)
    {
        _embedding = embedding;
        _memoryRepo = memoryRepo;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query,
        int limit = 5,
        double minSimilarity = 0.7,
        string? category = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Search called with empty query");
            return Array.Empty<MemorySearchResult>();
        }

        // Step 1: Embed the query
        float[]? embedding = null;
        try
        {
            embedding = await _embedding.GenerateEmbeddingAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed during search; returning empty results");
            return Array.Empty<MemorySearchResult>();
        }

        if (embedding == null || embedding.Length == 0)
        {
            _logger.LogWarning("Failed to generate embedding for search query");
            return Array.Empty<MemorySearchResult>();
        }

        // Step 2: Search via pgvector cosine distance
        var vector = new Vector(embedding);
        var results = await _memoryRepo.SearchMemoriesAsync(
            vector, limit, minSimilarity, category, ct);

        _logger.LogInformation(
            "Semantic search for query (len={QueryLen}) returned {Count} results",
            query.Length, results.Count);

        return results;
    }
}