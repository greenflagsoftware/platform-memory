namespace AgentMemory.Distillation;

/// <summary>
/// Distills a raw capture (plus optional surrounding context) into a
/// self-contained, standalone fact suitable for storage as a memory.
///
/// The distiller may return NONE (DistilledContent = null) when the capture
/// contains no durable fact worth remembering — e.g. bare acknowledgements,
/// command logs, or trivial reads.
/// </summary>
public interface IDistillationService
{
    /// <summary>
    /// Distill a capture's raw content into a standalone fact.
    /// </summary>
    /// <param name="rawContent">The raw capture text from the hook.</param>
    /// <param name="context">Optional surrounding context (previous turns, transcript excerpts,
    /// session metadata). Used by the LLM to produce a self-contained statement.
    /// Bounded to ~2k characters by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A DistillationResult. DistilledContent is null when NONE (no durable fact).</returns>
    Task<DistillationResult> DistillAsync(
        string rawContent,
        string? context = null,
        CancellationToken ct = default);
}