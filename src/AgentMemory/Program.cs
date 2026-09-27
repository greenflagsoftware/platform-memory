using AgentMemory.Capture;
using AgentMemory.Classification;
using AgentMemory.Embedding;
using AgentMemory.Processing;
using AgentMemory.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.MigrationsHistoryTable("__ef_migrations_history");
            npgsqlOptions.UseVector();
        }
    ));

// ── Services ──────────────────────────────────────────────────────
builder.Services.AddHttpClient<IClassificationService, ClassificationService>(client => { })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.DelayGenerator = static args =>
        {
            var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, args.AttemptNumber));
            return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 10)));
        };
        options.Retry.ShouldRetryAfterHeader = true;
    });

builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>(client => { })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.DelayGenerator = static args =>
        {
            var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, args.AttemptNumber));
            return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 10)));
        };
        options.Retry.ShouldRetryAfterHeader = true;
    });

builder.Services.AddScoped<CaptureProcessor>();
builder.Services.AddScoped<ICaptureRepository, CaptureRepository>();
builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();

// ── HTTP pipeline ─────────────────────────────────────────────────
var app = builder.Build();

// ── Database initialization (development-only; use migrations in production) ──
if (app.Environment.IsDevelopment())
{
    // Step 1: Bootstrap the pgvector extension before EF Core validates its model.
    var connString = app.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrEmpty(connString))
    {
        await using var bootConn = new Npgsql.NpgsqlConnection(connString);
        await bootConn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", bootConn);
        await cmd.ExecuteNonQueryAsync();
    }

    // Step 2: Recreate schema for dev (EnsureCreatedAsync is a no-op on existing tables).
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// ── Endpoints ─────────────────────────────────────────────────────
app.MapGet("/health", async (AppDbContext db) =>
{
    try
    {
        // Verify DB connectivity as part of the health check
        await db.Database.CanConnectAsync();
        return Results.Ok(new
        {
            status = "healthy",
            service = "agent-memory",
            version = "0.2.0",
            database = "connected"
        });
    }
    catch (Exception)
    {
        return Results.Ok(new
        {
            status = "degraded",
            service = "agent-memory",
            version = "0.2.0",
            database = "unreachable"
        });
    }
})
.WithName("HealthCheck");

app.MapCaptureEndpoints();
app.MapAdminEndpoints();

app.Run();