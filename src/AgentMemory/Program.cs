using AgentMemory.Capture;
using AgentMemory.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__ef_migrations_history")
    ));

// ── HTTP pipeline ─────────────────────────────────────────────────
var app = builder.Build();

// ── Database initialization (development-only; use migrations in production) ──
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Endpoints ─────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "agent-memory", version = "0.1.0" }))
   .WithName("HealthCheck");

app.MapCaptureEndpoints();

app.Run();