namespace AgentMemory.Classification;

/// <summary>
/// Result of a JEV classification call for a captured event.
/// </summary>
public record ClassificationResult(
    string Category,
    double SaveWorthiness,
    string? Error
);

/// <summary>
/// Classifies captured events using JEV (via the OpenRouter-hosted TypeSafe API).
/// </summary>
public interface IClassificationService
{
    Task<ClassificationResult> ClassifyAsync(string content, CancellationToken ct = default);
}