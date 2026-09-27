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
builder.Services.AddHttpClient<ClassificationService>();
builder.Services.AddHttpClient<EmbeddingService>();
builder.Services.AddScoped<CaptureProcessor>();

// ── HTTP pipeline ─────────────────────────────────────────────────
var app = builder.Build();

// ── Database initialization (development-only; use migrations in production) ──
if (app.Environment.IsDevelopment())
{
    // Step 1: Bootstrap the pgvector extension using a raw ADO.NET connection.
    // This must happen before EF Core's model validation runs because UseVector()
    // queries the database type catalog for 'vector', which fails if the extension
    // hasn't been installed yet.
    var connString = app.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrEmpty(connString))
    {
        await using var bootConn = new Npgsql.NpgsqlConnection(connString);
        await bootConn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", bootConn);
        await cmd.ExecuteNonQueryAsync();
    }

    // Step 2: Wipe and recreate schema so the new memories table is created.
    // EnsureCreatedAsync is a no-op on an existing schema, so we delete first.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// ── Endpoints ─────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "agent-memory", version = "0.2.0" }))
   .WithName("HealthCheck");

app.MapCaptureEndpoints();
app.MapAdminEndpoints();

app.Run();