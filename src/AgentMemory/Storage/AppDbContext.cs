using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace AgentMemory.Storage;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<CaptureRecord> Captures => Set<CaptureRecord>();
    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The pgvector extension must be installed before this runs
        // (bootstrapped in Program.cs via raw ADO.NET). This call registers
        // the extension with EF Core so subsequent migrations know about it.
        modelBuilder.HasPostgresExtension("vector");

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

        modelBuilder.Entity<MemoryRecord>(entity =>
        {
            entity.ToTable("memories");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                  .HasColumnName("id");

            entity.Property(e => e.CaptureId)
                  .HasColumnName("capture_id")
                  .IsRequired();

            entity.Property(e => e.Category)
                  .HasColumnName("category")
                  .IsRequired();

            entity.Property(e => e.Score)
                  .HasColumnName("score")
                  .IsRequired();

            entity.Property(e => e.Content)
                  .HasColumnName("content")
                  .IsRequired();

            entity.Property(e => e.Embedding)
                  .HasColumnName("embedding")
                  .HasColumnType("vector(1536)")
                  .IsRequired();

            entity.Property(e => e.CreatedAt)
                  .HasColumnName("created_at")
                  .HasDefaultValueSql("now()");

            // Phase 3: dedup columns
            entity.Property(e => e.LastSeenAt)
                  .HasColumnName("last_seen_at")
                  .HasDefaultValueSql("now()");

            entity.Property(e => e.SeenCount)
                  .HasColumnName("seen_count")
                  .HasDefaultValue(1);

            entity.HasOne(e => e.Capture)
                  .WithMany()
                  .HasForeignKey(e => e.CaptureId)
                  .OnDelete(DeleteBehavior.Cascade);

            // HNSW index for cosine similarity search
            entity.HasIndex(e => e.Embedding)
                  .HasDatabaseName("memories_embedding_hnsw")
                  .HasMethod("hnsw")
                  .HasOperators("vector_cosine_ops")
                  .HasStorageParameter("m", 16)
                  .HasStorageParameter("ef_construction", 64);
        });
    }
}