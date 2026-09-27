namespace AgentMemory.Storage;

public interface IMemoryRepository
{
    Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default);

    /// <summary>
    /// Search memories by cosine similarity to the given embedding vector.
    /// Returns up to <paramref name="limit"/> results ordered by descending similarity,
    /// optionally filtered by <paramref name="category"/>.
    /// </summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchMemoriesAsync(
        Pgvector.Vector queryEmbedding,
        int limit = 5,
        double minSimilarity = 0.7,
        string? category = null,
        CancellationToken ct = default
    );
}