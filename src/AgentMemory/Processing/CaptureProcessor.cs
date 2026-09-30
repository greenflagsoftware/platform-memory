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
/// - Phase 1d: Minimum-content gate (server-side, before the LLM call); acknowledgements
///   are rejected only on a whole-message match, not a substring match
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
    /// Bare acknowledgements / confirmations that carry no durable fact. A capture is
    /// rejected only when its ENTIRE text (ignoring case, punctuation and extra whitespace)
    /// equals one of these — never on a substring match, so "hooks", "token" or
    /// "abandoned" are not mistaken for "ok" or "done".
    /// </summary>
    private static readonly string[] DefaultRejectionPatterns =
    [
        "yes",
        "yes commit and push",
        "yes please",
        "ok",
        "okay",
        "go ahead",
        "commit and push",
        "done",
        "proceed",
        "continue",
        "thanks",
        "thank you",
        "sounds good",
        "lgtm",
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

            var memory = await ProcessCaptureDryAsync(capture, ct);
            if (memory == null)
                return;

            await StoreWithDedupAsync(memory, ct);
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
    /// Phase 3: persist a processed memory, first merging it into an existing memory whose
    /// embedding is within <c>Memory:DedupSimilarity</c> (bumps seen_count / last_seen_at and
    /// keeps the higher score) instead of inserting a near-duplicate row.
    /// Shared by the live capture path and the reprocess admin endpoint.
    /// </summary>
    public async Task<StoreOutcome> StoreWithDedupAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        var dedupSimilarity = _configuration.GetValue<double>(DedupSimilarityConfigKey, DefaultDedupSimilarity);
        var existingResults = await _memoryRepo.SearchMemoriesAsync(
            memory.Embedding,
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
                existingMemory.SeenCount += 1;
                if (memory.Score > existingMemory.Score)
                {
                    existingMemory.Score = memory.Score;
                }
                await _memoryRepo.UpdateMemoryAsync(existingMemory, ct);

                _logger.LogInformation(
                    "Deduplicated capture {CaptureId}: merged into existing memory id={ExistingId} " +
                    "(seen_count={SeenCount}, score={Score:F2})",
                    memory.CaptureId, existingMemory.Id, existingMemory.SeenCount, existingMemory.Score);
                return StoreOutcome.Deduplicated;
            }
            _logger.LogWarning(
                "Dedup hit for capture {CaptureId}: found memory id={ExistingId} but could not load it; " +
                "inserting new memory",
                memory.CaptureId, existing.Id);
        }

        await _memoryRepo.AddMemoryAsync(memory, ct);

        _logger.LogInformation(
            "Stored memory id={MemoryId} for capture {CaptureId}: category={Category}, " +
            "score={Score:F2}, content_len={ContentLen}",
            memory.Id, memory.CaptureId, memory.Category, memory.Score, memory.Content.Length);
        return StoreOutcome.Stored;
    }

    /// <summary>
    /// Run the full processing pipeline for a capture without persisting.
    /// Returns a MemoryRecord ready for storage, or null if nothing should be stored
    /// (skipped by a decision OR failed — use <see cref="EvaluateCaptureAsync"/> when the
    /// difference matters, as it does for anything destructive).
    /// </summary>
    public async Task<MemoryRecord?> ProcessCaptureDryAsync(
        CaptureRecord capture,
        CancellationToken ct = default)
        => (await EvaluateCaptureAsync(capture, ct)).Memory;

    /// <summary>
    /// Run the full pipeline (gate → classify → threshold → distill → embed) without
    /// persisting, and say WHY nothing would be stored: a deliberate decision
    /// (<see cref="ProcessResult.Skipped"/>) versus a transient failure such as an OpenRouter
    /// timeout or rate limit (<see cref="ProcessResult.Failed"/>). Reprocess must not treat
    /// the latter as "no longer worth remembering" and delete the existing memory.
    /// </summary>
    public async Task<ProcessResult> EvaluateCaptureAsync(
        CaptureRecord capture,
        CancellationToken ct = default)
    {
        // ── Phase 1d: Minimum-content gate (before LLM call) ──────
        var skipReason = CheckGate(capture.RawContent);
        if (skipReason != null)
        {
            _logger.LogInformation(
                "Capture {CaptureId} rejected by gate: {SkipReason}",
                capture.Id, skipReason);
            return ProcessResult.Skipped($"gate: {skipReason}");
        }

        // Step 1: Classify
        var classification = await _classification.ClassifyAsync(capture.RawContent, ct);
        if (classification.Error != null)
        {
            _logger.LogWarning(
                "Classification returned error for capture {CaptureId}: {Error}; skipping",
                capture.Id, classification.Error);
            return ProcessResult.Failed($"classification error: {classification.Error}");
        }

        // Step 2: Threshold check
        var threshold = _configuration.GetValue<double>(SaveThresholdConfigKey, DefaultSaveThreshold);
        if (classification.SaveWorthiness < threshold)
        {
            _logger.LogInformation(
                "Capture {CaptureId} score {Score:F2} below threshold {Threshold:F2}; skipping memory",
                capture.Id, classification.SaveWorthiness, threshold);
            return ProcessResult.Skipped(
                $"score {classification.SaveWorthiness:F2} below threshold {threshold:F2}");
        }

        // ── Phase 4: Distill the capture into a standalone fact ──
        var context = ExtractContext(capture.Metadata);
        var distillation = await _distillation.DistillAsync(capture.RawContent, context, ct);

        if (distillation.Error != null)
        {
            _logger.LogWarning(
                "Distillation returned error for capture {CaptureId}: {Error}; skipping memory",
                capture.Id, distillation.Error);
            return ProcessResult.Failed($"distillation error: {distillation.Error}");
        }

        // NONE: the distiller found no durable fact. Do not fall back to the raw capture —
        // that would store exactly the noise (tool logs, fragments) the distiller rejected.
        if (distillation.DistilledContent == null)
        {
            _logger.LogInformation(
                "Capture {CaptureId} distilled to NONE (no durable fact); skipping memory",
                capture.Id);
            return ProcessResult.Skipped("distiller returned NONE (no durable fact)");
        }

        var contentToStore = distillation.DistilledContent;
        var sourceExcerpt = capture.RawContent;

        // Step 3: Generate embedding from the distilled (or raw) content
        var embedding = await _embedding.GenerateEmbeddingAsync(contentToStore, ct);
        if (embedding == null)
        {
            _logger.LogWarning(
                "Embedding generation returned null for capture {CaptureId}; skipping memory",
                capture.Id);
            return ProcessResult.Failed("embedding generation failed");
        }

        return ProcessResult.Ok(new MemoryRecord
        {
            CaptureId = capture.Id,
            Category = classification.Category,
            Score = classification.SaveWorthiness,
            Content = contentToStore,
            SourceExcerpt = sourceExcerpt,
            Embedding = new Vector(embedding),
        });
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

        // Whole-message acknowledgement rejection (case/punctuation-insensitive).
        var rejectionPatterns = _configuration.GetSection(RejectionPatternsConfigKey)
            .Get<string[]>() ?? DefaultRejectionPatterns;

        var normalized = NormalizeForMatch(rawContent);
        foreach (var pattern in rejectionPatterns)
        {
            if (normalized == NormalizeForMatch(pattern))
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
    /// Lower-cases, replaces punctuation with spaces and collapses whitespace, so
    /// "Yes, commit and push." and "yes commit  and push" compare equal.
    /// </summary>
    private static string NormalizeForMatch(string text)
    {
        var chars = text.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
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

/// <summary>Result of <see cref="CaptureProcessor.StoreWithDedupAsync"/>.</summary>
public enum StoreOutcome
{
    Stored,
    Deduplicated,
}

/// <summary>
/// Outcome of <see cref="CaptureProcessor.EvaluateCaptureAsync"/>: exactly one of a memory to
/// store, a deliberate skip, or a failure.
/// </summary>
public sealed record ProcessResult(MemoryRecord? Memory, string? SkipReason, string? FailureReason)
{
    public bool IsFailure => FailureReason != null;

    public static ProcessResult Ok(MemoryRecord memory) => new(memory, null, null);
    public static ProcessResult Skipped(string reason) => new(null, reason, null);
    public static ProcessResult Failed(string reason) => new(null, null, reason);
}
