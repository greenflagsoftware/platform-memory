using System.Text.Json;
using System.Text.Json.Serialization;
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
            IMemoryRepository memoryRepo,
            CaptureProcessor processor,
            CancellationToken ct) =>
        {
            var dryRun = request.DryRun ?? true;

            List<CaptureRecord> captures;
            if (request.CaptureIds is { Length: > 0 })
            {
                var captureIds = new HashSet<long>(request.CaptureIds);
                captures = await db.Captures
                    .Where(c => captureIds.Contains(c.Id))
                    .OrderBy(c => c.Id)
                    .ToListAsync(ct);
            }
            else
            {
                captures = await db.Captures.OrderBy(c => c.Id).ToListAsync(ct);
            }

            var summary = await ReprocessHelper.RunReprocessAsync(captures, dryRun, memoryRepo, processor, ct);
            return Results.Ok(summary);
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
// Request bodies are snake_case like the rest of the API (CaptureRequest, the responses).
// Without the attributes, minimal APIs bind camelCase only and silently ignore "dry_run".
public record PruneRequest(
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("max_score")] double? MaxScore = null,
    [property: JsonPropertyName("ids")] long[]? Ids = null,
    [property: JsonPropertyName("dry_run")] bool? DryRun = true
);

// ── Phase 5b: Reprocess request type ─────────────────────────────
public record ReprocessRequest(
    [property: JsonPropertyName("capture_ids")] long[]? CaptureIds = null,
    [property: JsonPropertyName("dry_run")] bool? DryRun = true
);

// ── Phase 5b: Reprocess logic ────────────────────────────────────
public record ReprocessMemorySnapshot(string Content, string Category, double Score);

public record ReprocessItem(
    [property: JsonPropertyName("capture_id")] long CaptureId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("old")] ReprocessMemorySnapshot? Old,
    [property: JsonPropertyName("new")] ReprocessMemorySnapshot? New,
    // Only set on a real run: "stored" or "deduplicated" (dry runs don't preview dedup).
    [property: JsonPropertyName("outcome")] string? Outcome);

public record ReprocessSummary(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("total_captures")] int TotalCaptures,
    [property: JsonPropertyName("total_changes")] int TotalChanges,
    [property: JsonPropertyName("total_errors")] int TotalErrors,
    [property: JsonPropertyName("results")] IReadOnlyList<ReprocessItem> Results);

/// <summary>
/// Run the full processing pipeline for a batch of captures, replacing existing
/// memories with freshly processed results. Dry-run by default.
///
/// Safety rules, because this deletes data:
/// - A memory is only deleted when the pipeline DECIDES the capture isn't worth keeping
///   (gate, threshold, distiller NONE). A pipeline FAILURE (OpenRouter timeout, 429, no
///   embedding) leaves the existing memory alone and is reported as an "error" item.
/// - If storing the replacement fails after the old memory was deleted, the old memory is
///   put back. One bad capture is reported and skipped, not allowed to abort the batch.
/// </summary>
public static class ReprocessHelper
{
    public static async Task<ReprocessSummary> RunReprocessAsync(
        IReadOnlyList<CaptureRecord> captures,
        bool dryRun,
        IMemoryRepository memoryRepo,
        CaptureProcessor processor,
        CancellationToken ct)
    {
        var results = new List<ReprocessItem>();

        foreach (var capture in captures)
        {
            ct.ThrowIfCancellationRequested();

            var existing = new List<MemoryRecord>();
            try
            {
                existing = (await memoryRepo.GetByCaptureIdAsync(capture.Id, ct)).ToList();
                var latest = existing.Count > 0 ? existing[0] : null;
                var old = latest == null
                    ? null
                    : new ReprocessMemorySnapshot(latest.Content, latest.Category, latest.Score);

                var evaluated = await processor.EvaluateCaptureAsync(capture, ct);

                if (evaluated.IsFailure)
                {
                    // Not a decision: keep whatever is stored and say so.
                    results.Add(new ReprocessItem(
                        capture.Id, "error", evaluated.FailureReason, old, null,
                        existing.Count > 0 ? "kept_old" : null));
                    continue;
                }

                var newMemory = evaluated.Memory;
                if (newMemory == null)
                {
                    // The pipeline decided this is not worth remembering: any stored memory is stale.
                    if (existing.Count > 0)
                    {
                        if (!dryRun)
                        {
                            await memoryRepo.DeleteMemoriesAsync(existing, ct);
                        }

                        results.Add(new ReprocessItem(
                            capture.Id, "delete_old_memory", evaluated.SkipReason,
                            old, null, dryRun ? null : "deleted"));
                    }
                    continue;
                }

                var changed = latest == null
                    || newMemory.Category != latest.Category
                    || Math.Abs(newMemory.Score - latest.Score) > 0.01
                    || newMemory.Content != latest.Content;
                if (!changed)
                {
                    continue;
                }

                string? outcome = null;
                if (!dryRun)
                {
                    outcome = await ReplaceAsync(existing, newMemory, memoryRepo, processor, ct);
                }

                results.Add(new ReprocessItem(
                    capture.Id, latest == null ? "create" : "update", null,
                    old,
                    new ReprocessMemorySnapshot(newMemory.Content, newMemory.Category, newMemory.Score),
                    outcome));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ReprocessItem(
                    capture.Id, "error", $"{ex.GetType().Name}: {ex.Message}",
                    null, null, existing.Count > 0 ? "kept_or_restored_old" : null));
            }
        }

        var errors = results.Count(r => r.Action == "error");
        return new ReprocessSummary(
            dryRun ? "dry_run" : errors > 0 ? "reprocessed_with_errors" : "reprocessed",
            dryRun,
            captures.Count,
            results.Count - errors,
            errors,
            results);
    }

    /// <summary>
    /// Delete the old rows and store the replacement (with dedup). If storing throws, put the
    /// old rows back before rethrowing so a failure never costs a memory.
    /// </summary>
    private static async Task<string> ReplaceAsync(
        List<MemoryRecord> existing,
        MemoryRecord newMemory,
        IMemoryRepository memoryRepo,
        CaptureProcessor processor,
        CancellationToken ct)
    {
        // Remove the old rows first so the new memory can't dedup against itself.
        if (existing.Count > 0)
        {
            await memoryRepo.DeleteMemoriesAsync(existing, ct);
        }

        try
        {
            return (await processor.StoreWithDedupAsync(newMemory, ct)) == StoreOutcome.Deduplicated
                ? "deduplicated"
                : "stored";
        }
        catch
        {
            foreach (var old in existing)
            {
                // Re-insert as a fresh row (new id) with the same content and history.
                await memoryRepo.AddMemoryAsync(new MemoryRecord
                {
                    CaptureId = old.CaptureId,
                    Category = old.Category,
                    Score = old.Score,
                    Content = old.Content,
                    Embedding = old.Embedding,
                    SourceExcerpt = old.SourceExcerpt,
                    CreatedAt = old.CreatedAt,
                    LastSeenAt = old.LastSeenAt,
                    SeenCount = old.SeenCount,
                }, CancellationToken.None);
            }
            throw;
        }
    }
}
