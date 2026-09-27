namespace AgentMemory.Storage;

/// <summary>
/// Abstraction over capture storage for testability.
/// </summary>
public interface ICaptureRepository
{
    Task<CaptureRecord?> GetByIdAsync(long id, CancellationToken ct = default);
}

public class CaptureRepository : ICaptureRepository
{
    private readonly AppDbContext _db;

    public CaptureRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CaptureRecord?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        return await _db.Captures.FindAsync(new object[] { id }, ct);
    }
}