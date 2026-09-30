using AgentMemory.Capture;
using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using AgentMemory.Processing;
using AgentMemory.Retrieval;
using AgentMemory.Storage;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

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

// ── Phase 4: Distillation service ─────────────────────────────────
builder.Services.AddHttpClient<IDistillationService, DistillationService>(client => { })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
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
builder.Services.AddScoped<IRetrievalService, RetrievalService>();
builder.Services.AddScoped<IDistillationService, DistillationService>();

// ── MCP server (real JSON-RPC/SSE transport) ────────────────────
// Registered alongside the existing plain HTTP endpoints. The /capture, /admin/*,
// /search, and /search/context endpoints stay plain HTTP; only agent-facing tools
// are exposed via the MCP transport. Claude Code connects with:
//   claude mcp add --transport http http://localhost:5098/mcp
builder.Services.AddMcpServer()
    .WithTools<McpTools>()
    .WithHttpTransport(options =>
    {
        // SSE endpoint is /mcp by default (mapped via MapMcp("/mcp") below)
        // The HttpServerTransportOptions controls session management behavior.
    });

// ── HTTP pipeline ─────────────────────────────────────────────────
var app = builder.Build();

// ── Database initialization: bootstrap pgvector, then apply EF Core migrations ──
// Runs in every environment so `docker compose up` and a bare `dotnet run` both end up
// with a current schema; see POST /admin/migrate for applying migrations on demand
// instead (e.g. deploys where auto-migrate-on-startup is turned off).
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

    // Step 2: Apply pending migrations.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// ── Static dashboard (wwwroot/dashboard.html, served at /dashboard.html) ──
app.UseStaticFiles();

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
            version = "0.4.0",
            database = "connected"
        });
    }
    catch (Exception)
    {
        return Results.Ok(new
        {
            status = "degraded",
            service = "agent-memory",
            version = "0.4.0",
            database = "unreachable"
        });
    }
})
.WithName("HealthCheck");

app.MapCaptureEndpoints();
app.MapAdminEndpoints();
app.MapSearchEndpoints();

// ── MCP HTTP/SSE transport ──────────────────────────────────────
// Real JSON-RPC MCP transport for auto-discovery by Claude Code.
// The .WithTools<McpTools>() registration above provides search_memories.
// Connect via: claude mcp add --transport http http://localhost:5098/mcp
app.MapMcp("/mcp");

app.Run();