using System.Text.Json;
using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using AgentMemory.Storage;
using Pgvector;

namespace AgentMemory.Processing;

/// <summary>
/// Background pipeline for a single capture: classify → threshold → distill → embed → dedup → store.
/// Runs as an in-process fire-and-forget Task per capture.
///
/// Quality plan phases implemented here:
/// - Phase 1d: Minimum-content gate (server-side, before the LLM call)
/// - Phase 2a: Corrected threshold scale (default 2.5 on 0-4 scale)
/// - Phase 2d: Fixed error path (classification error no longer pretends to store)
/// - Phase 3: Deduplicate on write (search before insert, merge on hit)
/// - Phase 4: Distill captures into standalone facts via OpenRouter, embed the distilled content
/// </summary>
public class CaptureProcessor
{
    // Phase 2a: Default threshold is now 2.5 on the 0-4 scale (was 0.5 on 0-4)
    private const double DefaultSaveThreshold = 2.5;
    private const string SaveThresholdConfigKey = "Memory:SaveThreshold";

    // Phase 1d: Minimum-content gate configuration keys
    private const string MinWordsConfigKey = "Memory:Gate:MinWords";
    private const int DefaultMinWords = 3;
    private const string RejectionPatternsConfigKey = "Memory:Gate:RejectionPatterns";

    // Phase 3: Dedup configuration keys
    private const string DedupSimilarityConfigKey = "Memory:DedupSimilarity";
    private const double DefaultDedupSimilarity = 0.92;

    /// <summary>
    /// Patterns for low-value content that should be rejected before the LLM call.
    /// Matched case-insensitively against the capture's raw content.
    /// </summary>
    private static readonly string[] DefaultRejectionPatterns =
    [
        "yes, commit and push",
        "yes\b",
        "ok\b",
        "go ahead",
        "commit and push",
        "done\b",
        "proceed",
        "continue",
    ];

    private readonly ICaptureRepository _captureRepo;
    private readonly IClassificationService _classification;
    private readonly IDistillationService _distillation;
    private readonly IEmbeddingService _embedding;
    private readonly IMemoryRepository _memoryRepo;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CaptureProcessor> _logger;

    public CaptureProcessor(
        ICaptureRepository captureRepo,
        IClassificationService classification,
        IDistillationService distillation,
        IEmbeddingService embedding,
        IMemoryRepository memoryRepo,
        IConfiguration configuration,
        ILogger<CaptureProcessor> logger)
    {
        _classification = classification;
        _distillation = distillation;
        _embedding = embedding;
        _memoryRepo = memoryRepo;
        _captureRepo = captureRepo;
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
            var capture = await _captureRepo.GetByIdAsync(captureId, ct);
            if (capture == null)
            {
                _logger.LogWarning("Capture {CaptureId} not found for processing", captureId);
                return;
            }

            // ── Phase 1d: Minimum-content gate (before LLM call) ──────
            var skipReason = CheckGate(capture.RawContent);
            if (skipReason != null)
            {
                _logger.LogInformation(
                    "Capture {CaptureId} rejected by gate: {SkipReason}",
                    captureId, skipReason);
                return;
            }

            // Step 1: Classify
            var classification = await _classification.ClassifyAsync(capture.RawContent, ct);
            if (classification.Error != null)
            {
                // Phase 2d: Log the error and skip — no memory is stored.
                _logger.LogWarning(
                    "Classification returned error for capture {CaptureId}: {Error}; skipping",
                    captureId, classification.Error);
                return;
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

            // ── Phase 4: Distill the capture into a standalone fact ──
            // Extract optional context from capture metadata for the distillation call
            var context = ExtractContext(capture.Metadata);
            var distillation = await _distillation.DistillAsync(capture.RawContent, context, ct);

            if (distillation.Error != null)
            {
                _logger.LogWarning(
                    "Distillation returned error for capture {CaptureId}: {Error}; skipping memory",
                    captureId, distillation.Error);
                return;
            }

            // The content to store and embed is the distilled fact (or the raw content
            // if distillation returned NONE — which shouldn't happen post-classification
            // but handle gracefully)
            var contentToStore = distillation.DistilledContent ?? capture.RawContent;
            var sourceExcerpt = distillation.DistilledContent != null
                ? capture.RawContent
                : null;

            // Step 3: Generate embedding from the distilled (or raw) content
            var embedding = await _embedding.GenerateEmbeddingAsync(contentToStore, ct);
            if (embedding == null)
            {
                _logger.LogWarning(
                    "Embedding generation returned null for capture {CaptureId}; skipping memory",
                    captureId);
                return;
            }

            // ── Phase 3: Deduplicate on write ───────────────────────
            var dedupSimilarity = _configuration.GetValue<double>(DedupSimilarityConfigKey, DefaultDedupSimilarity);
            var existingResults = await _memoryRepo.SearchMemoriesAsync(
                new Vector(embedding),
                limit: 1,
                minSimilarity: dedupSimilarity,
                ct: ct);

            if (existingResults.Count > 0)
            {
                var existing = existingResults[0];
                var existingMemory = await _memoryRepo.GetByIdAsync(existing.Id, ct);
                if (existingMemory != null)
                {
                    existingMemory.LastSeenAt = DateTimeOffset.UtcNow;
                    existingMemory.SeenCount = existingMemory.SeenCount + 1;
                    // Keep the higher score
                    if (classification.SaveWorthiness > existingMemory.Score)
                    {
                        existingMemory.Score = classification.SaveWorthiness;
                    }
                    await _memoryRepo.UpdateMemoryAsync(existingMemory, ct);

                    _logger.LogInformation(
                        "Deduplicated capture {CaptureId}: merged into existing memory id={ExistingId} " +
                        "(seen_count={SeenCount}, score={Score:F2})",
                        captureId, existingMemory.Id, existingMemory.SeenCount, existingMemory.Score);
                    return;
                }
                _logger.LogWarning(
                    "Dedup hit for capture {CaptureId}: found memory id={ExistingId} but could not load it; " +
                    "inserting new memory",
                    captureId, existing.Id);
            }

            // Step 4: Store memory via repository
            var memory = new MemoryRecord
            {
                CaptureId = capture.Id,
                Category = classification.Category,
                Score = classification.SaveWorthiness,
                Content = contentToStore,
                SourceExcerpt = sourceExcerpt,
                Embedding = new Vector(embedding),
            };

            await _memoryRepo.AddMemoryAsync(memory, ct);

            _logger.LogInformation(
                "Stored memory id={MemoryId} for capture {CaptureId}: category={Category}, " +
                "score={Score:F2}, content_len={ContentLen}",
                memory.Id, captureId, memory.Category, memory.Score, memory.Content.Length);
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

    /// <summary>
    /// Check whether a capture's raw content passes the minimum-content gate.
    /// Returns null if accepted, or a string reason if rejected.
    /// Configurable via Memory:Gate:MinWords and Memory:Gate:RejectionPatterns.
    /// </summary>
    private string? CheckGate(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return "empty_content";
        }

        // Pattern rejection (case-insensitive)
        var rejectionPatterns = _configuration.GetSection(RejectionPatternsConfigKey)
            .Get<string[]>() ?? DefaultRejectionPatterns;

        var contentLower = rawContent.ToLowerInvariant();
        foreach (var pattern in rejectionPatterns)
        {
            if (contentLower.Contains(pattern.ToLowerInvariant()))
            {
                return $"matched_rejection_pattern: '{pattern}'";
            }
        }

        // Minimum word count
        var minWords = _configuration.GetValue<int>(MinWordsConfigKey, DefaultMinWords);
        var words = rawContent
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count < minWords)
        {
            var hasCodeToken = words.Any(w =>
                w.Contains('_') || w.Contains('.') || w.Contains('/') ||
                w.Contains('\\') || w.Contains('(') || w.Contains(')') ||
                w.Contains('{') || w.Contains('}') || w.Contains('[') ||
                w.Contains(']') || w.Contains('<') || w.Contains('>') ||
                w.Contains('=') || w.Contains(':') || w.Contains('@') ||
                w.Length > 20);

            if (!hasCodeToken)
            {
                return $"below_min_words({words.Count} < {minWords})";
            }
        }

        return null; // accepted
    }

    /// <summary>
    /// Extract a bounded context string from the capture's JSON metadata.
    /// Looks for a "context" or "previous_turns" field; limits to ~2000 chars.
    /// </summary>
    private static string? ExtractContext(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata) || metadata == "{}")
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;

            // Try "context" first, then "previous_turns"
            if (root.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.String)
            {
                var text = ctx.GetString() ?? "";
                return text.Length > 2000 ? text[..2000] : text;
            }

            if (root.TryGetProperty("previous_turns", out var turns) && turns.ValueKind == JsonValueKind.String)
            {
                var text = turns.GetString() ?? "";
                return text.Length > 2000 ? text[..2000] : text;
            }
        }
        catch
        {
            // Best-effort parse
        }

        return null;
    }
}