using System.Text.Json;
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
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(m => m.Category == category);
            }

            var memories = await query
                .Take(limit)
                .Select(m => new
                {
                    m.Id,
                    m.Category,
                    m.Score,
                    m.Content,
                    m.CreatedAt,
                    m.LastSeenAt,
                    m.SeenCount
                })
                .ToListAsync();

            return Results.Ok(memories);
        })
        .WithName("AdminListMemories");

        // ── Phase 5a: DELETE /admin/memories/{id} ───────────────────
        group.MapDelete("/memories/{id:long}", async (
            long id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var memory = await db.Memories.FindAsync([id], ct);
            if (memory == null)
            {
                return Results.NotFound(new { error = $"Memory {id} not found" });
            }

            db.Memories.Remove(memory);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                status = "deleted",
                memory_id = id
            });
        })
        .WithName("AdminDeleteMemory");

        // ── Phase 5a: POST /admin/memories/prune ────────────────────
        // Bulk-remove memories matching filters. Dry-run by default.
        // Filters: category, max_score (inclusive), ids (comma-separated).
        group.MapPost("/memories/prune", async (
            PruneRequest request,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.Memories.AsQueryable();

            if (!string.IsNullOrWhiteSpace(request.Category))
            {
                query = query.Where(m => m.Category == request.Category);
            }

            if (request.MaxScore.HasValue)
            {
                query = query.Where(m => m.Score <= request.MaxScore.Value);
            }

            if (request.Ids is { Length: > 0 })
            {
                var ids = new HashSet<long>(request.Ids);
                query = query.Where(m => ids.Contains(m.Id));
            }

            var matched = await query
                .Select(m => new { m.Id, m.Category, m.Score, m.Content, m.CreatedAt })
                .ToListAsync(ct);

            var dryRun = request.DryRun ?? true;

            if (!dryRun && matched.Count > 0)
            {
                // Remove all matched entities individually via EF Core
                var memoryIds = matched.Select(m => m.Id).ToList();
                var memoriesToDelete = await db.Memories
                    .Where(m => memoryIds.Contains(m.Id))
                    .ToListAsync(ct);
                db.Memories.RemoveRange(memoriesToDelete);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new
            {
                status = dryRun ? "dry_run" : "pruned",
                dry_run = dryRun,
                removed_count = matched.Count,
                memories = matched
            });
        })
        .WithName("AdminPruneMemories");

        // ── Phase 5b: POST /admin/memories/reprocess ────────────────
        // Re-runs the full pipeline (gate → classify → distill → embed → dedup) for
        // every capture (or a specific set). Deletes the existing memory for each
        // capture and replaces it with the freshly processed result.
        // Dry-run by default — reports what would change without making changes.
        group.MapPost("/memories/reprocess", async (
            ReprocessRequest request,
            AppDbContext db,
            CaptureProcessor processor,
            CancellationToken ct) =>
        {
            // Step 1: Determine which captures to reprocess
            if (request.CaptureIds is { Length: > 0 })
            {
                // Reprocess specific captures
                var captureIds = new HashSet<long>(request.CaptureIds);
                var captures = await db.Captures
                    .Where(c => captureIds.Contains(c.Id))
                    .OrderBy(c => c.Id)
                    .ToListAsync(ct);
                return await ReprocessHelper.RunReprocess(captures, request.DryRun ?? true, db, processor, ct);
            }

            // Reprocess ALL captures
            var allCaptures = await db.Captures
                .OrderBy(c => c.Id)
                .ToListAsync(ct);
            return await ReprocessHelper.RunReprocess(allCaptures, request.DryRun ?? true, db, processor, ct);
        })
        .WithName("AdminReprocessMemories");

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

        // ── Phase 6b: GET /admin/stats ────────────────────────────
        // Returns store statistics: counts by category, score histogram,
        // dedupe counts, and capture processing summary.
        group.MapGet("/stats", async (
            AppDbContext db,
            CancellationToken ct) =>
        {
            var totalMemories = await db.Memories.CountAsync(ct);
            var totalCaptures = await db.Captures.CountAsync(ct);

            // Memories by category
            var byCategory = await db.Memories
                .GroupBy(m => m.Category)
                .Select(g => new { category = g.Key, count = g.Count() })
                .ToListAsync(ct);

            // Score histogram: buckets [0,1), [1,2), [2,3), [3,4], and unknown
            var scoreBuckets = await db.Memories
                .GroupBy(m => m.Score < 1 ? "0-0.9" :
                              m.Score < 2 ? "1-1.9" :
                              m.Score < 3 ? "2-2.9" :
                              "3-4")
                .Select(g => new { bucket = g.Key, count = g.Count() })
                .ToListAsync(ct);

            // Dedup stats
            var dedupCount = await db.Memories.CountAsync(m => m.SeenCount > 1, ct);
            var totalSeenCount = await db.Memories.SumAsync(m => m.SeenCount, ct);

            // Captures that didn't result in a memory
            var capturesWithoutMemory = await db.Captures
                .CountAsync(c => !db.Memories.Any(m => m.CaptureId == c.Id), ct);

            return Results.Ok(new
            {
                total_captures = totalCaptures,
                total_memories = totalMemories,
                memories_by_category = byCategory,
                score_histogram = scoreBuckets,
                dedup = new
                {
                    consolidated_count = dedupCount,
                    total_seen_count = totalSeenCount
                },
                captures_without_memory = capturesWithoutMemory
            });
        })
        .WithName("AdminStats");
    }
}

// ── Phase 5a: Prune request type ─────────────────────────────────
public record PruneRequest(
    string? Category = null,
    double? MaxScore = null,
    long[]? Ids = null,
    bool? DryRun = true
);

// ── Phase 5b: Reprocess request type ─────────────────────────────
public record ReprocessRequest(
    long[]? CaptureIds = null,
    bool? DryRun = true
);

// ── Phase 5b: Reprocess logic ────────────────────────────────────
/// <summary>
/// Run the full processing pipeline for a batch of captures, replacing existing
/// memories with freshly processed results. Dry-run by default.
/// </summary>
internal static class ReprocessHelper
{
    internal static async Task<IResult> RunReprocess(
        List<CaptureRecord> captures,
        bool dryRun,
        AppDbContext db,
        CaptureProcessor processor,
        CancellationToken ct)
    {
        var results = new List<object>();
        var totalChanges = 0;

        foreach (var capture in captures)
        {
            // Get the current memory (if any) for this capture
            var existingMemory = await db.Memories
                .Where(m => m.CaptureId == capture.Id)
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);

            // Store the old state for dry-run comparison
            var oldContent = existingMemory?.Content;
            var oldCategory = existingMemory?.Category;
            var oldScore = existingMemory?.Score;

            // Process the capture through the full pipeline (in-memory, not persisted yet)
            var newMemory = await processor.ProcessCaptureDryAsync(capture, ct);

            if (newMemory == null)
            {
                // Capture didn't pass the pipeline — track whether we'd delete the old memory
                if (existingMemory != null)
                {
                    results.Add(new
                    {
                        capture_id = capture.Id,
                        action = "delete_old_memory",
                        reason = "no longer passes pipeline (gate/classify/distill)",
                        old = new { content = oldContent, category = oldCategory, score = oldScore },
                        _new = (object?)null
                    });
                    totalChanges++;
                }
                // else: wasn't stored before and still isn't — no change
                continue;
            }

            // Compare with existing
            var changed = existingMemory == null
                || newMemory.Category != oldCategory
                || Math.Abs(newMemory.Score - (oldScore ?? 0)) > 0.01
                || newMemory.Content != oldContent;

            if (changed)
            {
                results.Add(new
                {
                    capture_id = capture.Id,
                    action = existingMemory == null ? "create" : "update",
                    old = existingMemory == null
                        ? null
                        : (object)new { content = oldContent, category = oldCategory, score = oldScore },
                    _new = new
                    {
                        content = newMemory.Content,
                        category = newMemory.Category,
                        score = newMemory.Score,
                        source_excerpt = newMemory.SourceExcerpt
                    }
                });
                totalChanges++;
            }

            if (!dryRun && changed)
            {
                // Apply changes: delete old memory, keep captures_old content
                if (existingMemory != null)
                {
                    db.Memories.Remove(existingMemory);
                }

                // Insert the new memory
                db.Memories.Add(newMemory);
                await db.SaveChangesAsync(ct);
            }
        }

        var status = dryRun ? "dry_run" : "reprocessed";

        return Results.Ok(new
        {
            status,
            dry_run = dryRun,
            total_captures = captures.Count,
            total_changes = totalChanges,
            results
        });
    }
}