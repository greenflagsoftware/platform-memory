using AgentMemory.Processing;
using AgentMemory.Storage;
using Microsoft.EntityFrameworkCore;

namespace AgentMemory.Capture;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin");

        group.MapGet("/memories", async (
            AppDbContext db,
            int limit = 10,
            string? category = null) =>
        {
            var query = db.Memories
                .OrderByDescending(m => m.CreatedAt)
                .Take(limit)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(m => m.Category == category);
            }

            var memories = await query
                .Select(m => new
                {
                    m.Id,
                    m.Category,
                    m.Score,
                    m.Content,
                    m.CreatedAt
                })
                .ToListAsync();

            return Results.Ok(memories);
        })
        .WithName("AdminListMemories");

        // Re-runs classification + threshold + embedding for a capture that's already
        // stored. Useful after changing the classification model/prompt or the save
        // threshold, or to retry a capture whose classification/embedding call failed.
        // This adds a new `memories` row if the (re-)classification clears the
        // threshold; it does not remove any memory already stored for this capture.
        group.MapPost("/captures/{id:long}/reclassify", async (
            long id,
            AppDbContext db,
            CaptureProcessor processor,
            CancellationToken ct) =>
        {
            var exists = await db.Captures.AnyAsync(c => c.Id == id, ct);
            if (!exists)
            {
                return Results.NotFound(new { error = $"Capture {id} not found" });
            }

            await processor.ProcessCaptureAsync(id, ct);

            var latestMemory = await db.Memories
                .Where(m => m.CaptureId == id)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.Id, m.Category, m.Score, m.CreatedAt })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                capture_id = id,
                status = "reclassified",
                memory = latestMemory
            });
        })
        .WithName("AdminReclassifyCapture");

        // Applies any pending EF Core migrations. The server also migrates on startup
        // (see Program.cs), so this exists for on-demand use — e.g. after a deploy
        // where auto-migrate-on-startup is disabled, or to confirm the schema is current
        // without restarting the process.
        group.MapPost("/migrate", async (AppDbContext db, CancellationToken ct) =>
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);

            return Results.Ok(new
            {
                status = "migrated",
                applied = pending
            });
        })
        .WithName("AdminApplyMigrations");
    }
}