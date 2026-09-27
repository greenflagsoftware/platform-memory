using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace AgentMemory.Storage;

public class MemoryRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>FK to the raw capture this memory was derived from.</summary>
    public long CaptureId { get; set; }

    /// <summary>Category label from classification, e.g. "question", "coding", "tool_call".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Save-worthiness score from classification (0–1).</summary>
    public double Score { get; set; }

    /// <summary>Normalized content text that was embedded.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>pgvector embedding (1536 dimensions for text-embedding-3-small).</summary>
    [Column(TypeName = "vector(1536)")]
    public Vector Embedding { get; set; } = new Vector(new float[1536]);

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    [ForeignKey(nameof(CaptureId))]
    public CaptureRecord? Capture { get; set; }
}