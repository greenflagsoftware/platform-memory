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
    }
}