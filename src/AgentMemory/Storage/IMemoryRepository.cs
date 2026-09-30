namespace AgentMemory.Storage;

public interface IMemoryRepository
{
    /// <summary>
    /// Load a memory record by primary key.
    /// </summary>
    Task<MemoryRecord?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// All memories derived from a capture, newest first.
    /// </summary>
    Task<IReadOnlyList<MemoryRecord>> GetByCaptureIdAsync(long captureId, CancellationToken ct = default);

    /// <summary>
    /// Permanently remove the given memories.
    /// </summary>
    Task DeleteMemoriesAsync(IEnumerable<MemoryRecord> memories, CancellationToken ct = default);

    Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default);

    /// <summary>
    /// Phase 3: Update an existing memory (e.g. bump last_seen_at, seen_count).
    /// </summary>
    Task UpdateMemoryAsync(MemoryRecord memory, CancellationToken ct = default);

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