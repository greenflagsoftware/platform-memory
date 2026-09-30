namespace AgentMemory.Distillation;

/// <summary>
/// Result of distilling a capture into a standalone fact.
/// </summary>
public record DistillationResult(
    /// <summary>
    /// The distilled, self-contained fact. Null when the distiller determines
    /// there is no durable fact to extract (NONE).
    /// </summary>
    string? DistilledContent,
    /// <summary>
    /// Non-null when an error occurred.
    /// </summary>
    string? Error = null
);