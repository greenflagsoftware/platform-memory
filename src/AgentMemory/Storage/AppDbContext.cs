using Microsoft.EntityFrameworkCore;

namespace AgentMemory.Storage;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<CaptureRecord> Captures => Set<CaptureRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CaptureRecord>(entity =>
        {
            entity.ToTable("captures");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                  .HasColumnName("id");

            entity.Property(e => e.SessionId)
                  .HasColumnName("session_id")
                  .IsRequired();

            entity.Property(e => e.HookEvent)
                  .HasColumnName("hook_event")
                  .IsRequired();

            entity.Property(e => e.RawContent)
                  .HasColumnName("raw_content")
                  .IsRequired();

            entity.Property(e => e.Metadata)
                  .HasColumnName("metadata")
                  .HasColumnType("jsonb")
                  .HasDefaultValue("{}");

            entity.Property(e => e.CapturedAt)
                  .HasColumnName("captured_at")
                  .HasDefaultValueSql("now()");
        });

        // Ensure pgvector extension is created (safe to run even without the extension loaded)
        // This is a no-op in Phase 0 since we don't use vector columns yet,
        // but having it here prepares the DB for Phase 1.
    }
}