namespace AgentMemory.Storage;

/// <summary>
/// Abstraction over memory storage so CaptureProcessor is testable without real pgvector.
/// </summary>
public interface IMemoryRepository
{
    Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default);
}

public class MemoryRepository : IMemoryRepository
{
    private readonly AppDbContext _db;

    public MemoryRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        _db.Memories.Add(memory);
        await _db.SaveChangesAsync(ct);
    }
}