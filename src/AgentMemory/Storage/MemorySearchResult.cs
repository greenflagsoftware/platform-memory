namespace AgentMemory.Storage;

/// <summary>
/// A memory returned from a semantic search, with a similarity score.
/// </summary>
public record MemorySearchResult(
    long Id,
    string Category,
    double Score,
    string Content,
    DateTimeOffset CreatedAt,
    double Similarity
);