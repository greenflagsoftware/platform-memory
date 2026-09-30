using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace AgentMemory.Storage;

public class MemoryRepository : IMemoryRepository
{
    private readonly AppDbContext _db;

    public MemoryRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Load a memory record by primary key.
    /// </summary>
    public async Task<MemoryRecord?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        return await _db.Memories.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<MemoryRecord>> GetByCaptureIdAsync(long captureId, CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.CaptureId == captureId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task DeleteMemoriesAsync(IEnumerable<MemoryRecord> memories, CancellationToken ct = default)
    {
        _db.Memories.RemoveRange(memories);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        _db.Memories.Add(memory);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 3: Update an existing memory's metadata (last_seen_at, seen_count, score).
    /// </summary>
    public async Task UpdateMemoryAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        // Mark only the properties that should be updated on dedup
        _db.Memories.Attach(memory);
        _db.Entry(memory).Property(m => m.LastSeenAt).IsModified = true;
        _db.Entry(memory).Property(m => m.SeenCount).IsModified = true;
        _db.Entry(memory).Property(m => m.Score).IsModified = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchMemoriesAsync(
        Vector queryEmbedding,
        int limit = 5,
        double minSimilarity = 0.7,
        string? category = null,
        CancellationToken ct = default)
    {
        // Use raw ADO.NET with pgvector's <=> (cosine distance) operator.
        // EF Core's LINQ methods for pgvector are not available in Pgvector 0.3.0,
        // so we execute the HNSW-index-friendly query directly.
        var conn = _db.Database.GetDbConnection();

        // The DbContext's connection is shared by everything in this scope. Only open (and
        // later close) it if we are the ones who found it closed: opening an already-open
        // connection throws, and leaving it open made every second search in one scope
        // (e.g. a batch reprocess) fail with "Connection already open".
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            return await ExecuteSearchAsync(conn, queryEmbedding, limit, minSimilarity, category, ct);
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static async Task<IReadOnlyList<MemorySearchResult>> ExecuteSearchAsync(
        DbConnection conn,
        Vector queryEmbedding,
        int limit,
        double minSimilarity,
        string? category,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();

        var sql = """
            SELECT
                id,
                category,
                score,
                content,
                created_at,
                last_seen_at,
                seen_count,
                source_excerpt,
                1.0 - (embedding <=> @embedding) AS similarity
            FROM memories
            WHERE 1.0 - (embedding <=> @embedding) >= @min_similarity
            """;

        if (!string.IsNullOrWhiteSpace(category))
        {
            sql += "\n    AND category = @category";
            var catParam = cmd.CreateParameter();
            catParam.ParameterName = "category";
            catParam.Value = category;
            cmd.Parameters.Add(catParam);
        }

        sql += "\n    ORDER BY embedding <=> @embedding\n    LIMIT @limit";

        cmd.CommandText = sql;
        cmd.CommandType = System.Data.CommandType.Text;

        var embParam = cmd.CreateParameter();
        embParam.ParameterName = "embedding";
        embParam.Value = queryEmbedding;
        cmd.Parameters.Add(embParam);

        var simParam = cmd.CreateParameter();
        simParam.ParameterName = "min_similarity";
        simParam.Value = minSimilarity;
        cmd.Parameters.Add(simParam);

        var limParam = cmd.CreateParameter();
        limParam.ParameterName = "limit";
        limParam.Value = limit;
        cmd.Parameters.Add(limParam);

        var results = new List<MemorySearchResult>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MemorySearchResult(
                Id: reader.GetInt64(0),
                Category: reader.GetString(1),
                Score: (double)reader.GetFloat(2),
                Content: reader.GetString(3),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(4),
                LastSeenAt: reader.GetFieldValue<DateTimeOffset>(5),
                SeenCount: reader.GetInt32(6),
                SourceExcerpt: reader.IsDBNull(7) ? null : reader.GetString(7),
                Similarity: reader.GetDouble(8)
            ));
        }

        return results;
    }
}