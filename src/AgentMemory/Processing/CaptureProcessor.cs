using AgentMemory.Classification;
using AgentMemory.Embedding;
using AgentMemory.Storage;
using Pgvector;

namespace AgentMemory.Processing;

/// <summary>
/// Background pipeline for a single capture: classify → threshold → embed → store.
/// Runs as an in-process fire-and-forget Task per capture.
/// </summary>
public class CaptureProcessor
{
    private const double DefaultSaveThreshold = 0.5;
    private const string SaveThresholdConfigKey = "Memory:SaveThreshold";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClassificationService _classification;
    private readonly EmbeddingService _embedding;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CaptureProcessor> _logger;

    public CaptureProcessor(
        IServiceScopeFactory scopeFactory,
        ClassificationService classification,
        EmbeddingService embedding,
        IConfiguration configuration,
        ILogger<CaptureProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _classification = classification;
        _embedding = embedding;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Process a captured event in the background. Fails gracefully — the capture
    /// is already persisted; this is best-effort classification and storage.
    /// </summary>
    public async Task ProcessCaptureAsync(long captureId, CancellationToken ct = default)
    {
        try
        {
            // Re-resolve the DbContext via scope — this runs on a background thread
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var capture = await db.Captures.FindAsync(new object[] { captureId }, ct);
            if (capture == null)
            {
                _logger.LogWarning("Capture {CaptureId} not found for processing", captureId);
                return;
            }

            // Step 1: Classify
            var classification = await _classification.ClassifyAsync(capture.RawContent, ct);
            if (classification.Error != null)
            {
                _logger.LogWarning(
                    "Classification returned error for capture {CaptureId}: {Error}",
                    captureId, classification.Error);
                // Still record a low-score memory so we have a record of the attempt
            }

            // Step 2: Threshold check
            var threshold = _configuration.GetValue<double>(SaveThresholdConfigKey, DefaultSaveThreshold);
            if (classification.SaveWorthiness < threshold)
            {
                _logger.LogInformation(
                    "Capture {CaptureId} score {Score:F2} below threshold {Threshold:F2}; skipping memory",
                    captureId, classification.SaveWorthiness, threshold);
                return;
            }

            // Step 3: Generate embedding
            var embedding = await _embedding.GenerateEmbeddingAsync(capture.RawContent, ct);
            if (embedding == null)
            {
                _logger.LogWarning(
                    "Embedding generation returned null for capture {CaptureId}; skipping memory",
                    captureId);
                return;
            }

            // Step 4: Store memory
            var memory = new MemoryRecord
            {
                CaptureId = capture.Id,
                Category = classification.Category,
                Score = classification.SaveWorthiness,
                Content = capture.RawContent,
                Embedding = new Vector(embedding),
            };

            db.Memories.Add(memory);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Stored memory id={MemoryId} for capture {CaptureId}: category={Category}, score={Score:F2}",
                memory.Id, captureId, memory.Category, memory.Score);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Processing cancelled for capture {CaptureId}", captureId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed for capture {CaptureId}", captureId);
        }
    }
}