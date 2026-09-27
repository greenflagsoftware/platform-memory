namespace AgentMemory.Embedding;

/// <summary>
/// Generates embeddings via OpenRouter's embedding endpoint.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}