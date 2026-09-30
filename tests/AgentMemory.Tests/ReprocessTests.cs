using AgentMemory.Capture;
using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using AgentMemory.Processing;
using AgentMemory.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Pgvector;

namespace AgentMemory.Tests;

public class ReprocessTests
{
    private readonly Mock<IClassificationService> _classification = new();
    private readonly Mock<IDistillationService> _distillation = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly InMemoryCaptureRepository _captureRepo = new();
    private readonly InMemoryMemoryRepository _memoryRepo = new();
    private readonly CaptureProcessor _processor;

    public ReprocessTests()
    {
        _processor = new CaptureProcessor(
            _captureRepo,
            _classification.Object,
            _distillation.Object,
            _embedding.Object,
            _memoryRepo,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Memory:SaveThreshold"] = "2.5" })
                .Build(),
            new Mock<ILogger<CaptureProcessor>>().Object);

        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });
    }

    private CaptureRecord AddCapture(long id, string content = "We decided to use pgvector for all embeddings.")
    {
        var capture = new CaptureRecord
        {
            Id = id, SessionId = "s", HookEvent = "UserPromptSubmit", RawContent = content
        };
        _captureRepo.Captures[id] = capture;
        return capture;
    }

    private MemoryRecord AddMemory(long captureId, string content, double score = 3.0, string category = "decision")
    {
        var memory = new MemoryRecord
        {
            CaptureId = captureId,
            Category = category,
            Score = score,
            Content = content,
            Embedding = new Vector(new float[] { 0.1f, 0.2f, 0.3f }),
        };
        _memoryRepo.AddMemoryAsync(memory).GetAwaiter().GetResult();
        return memory;
    }

    private void PipelineYields(double score, string? distilled, string category = "decision")
    {
        _classification
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult(category, score, null));
        _distillation
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult(distilled));
    }

    private Task<ReprocessSummary> RunAsync(bool dryRun, params CaptureRecord[] captures) =>
        ReprocessHelper.RunReprocessAsync(captures, dryRun, _memoryRepo, _processor, CancellationToken.None);

    [Fact]
    public async Task Memory_That_No_Longer_Passes_Is_Deleted_On_A_Real_Run()
    {
        var capture = AddCapture(1);
        AddMemory(1, "stale noise memory");
        PipelineYields(score: 0.5, distilled: "Decision: irrelevant");   // now below threshold

        var summary = await RunAsync(dryRun: false, capture);

        Assert.Empty(_memoryRepo.Memories);
        var item = Assert.Single(summary.Results);
        Assert.Equal("delete_old_memory", item.Action);
        Assert.Equal("deleted", item.Outcome);
        Assert.Equal(1, summary.TotalChanges);
    }

    [Fact]
    public async Task Memory_That_No_Longer_Passes_Is_Reported_But_Kept_On_A_Dry_Run()
    {
        var capture = AddCapture(1);
        AddMemory(1, "stale noise memory");
        PipelineYields(score: 0.5, distilled: null);

        var summary = await RunAsync(dryRun: true, capture);

        Assert.Single(_memoryRepo.Memories);
        var item = Assert.Single(summary.Results);
        Assert.Equal("delete_old_memory", item.Action);
        Assert.Null(item.Outcome);
        Assert.Equal("dry_run", summary.Status);
    }

    [Fact]
    public async Task Distiller_NONE_Removes_The_Old_Memory()
    {
        var capture = AddCapture(1);
        AddMemory(1, "raw capture text stored before the NONE fix");
        PipelineYields(score: 3.5, distilled: null);   // passes threshold, distiller says NONE

        await RunAsync(dryRun: false, capture);

        Assert.Empty(_memoryRepo.Memories);
    }

    [Fact]
    public async Task Changed_Memory_Is_Replaced_On_A_Real_Run()
    {
        var capture = AddCapture(1);
        AddMemory(1, "old raw content", score: 3.0);
        PipelineYields(score: 3.5, distilled: "Decision: use pgvector for all embeddings.");

        var summary = await RunAsync(dryRun: false, capture);

        var memory = Assert.Single(_memoryRepo.Memories.Values);
        Assert.Equal("Decision: use pgvector for all embeddings.", memory.Content);
        var item = Assert.Single(summary.Results);
        Assert.Equal("update", item.Action);
        Assert.Equal("stored", item.Outcome);
    }

    [Fact]
    public async Task Dry_Run_Does_Not_Change_The_Store()
    {
        var capture = AddCapture(1);
        AddMemory(1, "old raw content");
        PipelineYields(score: 3.5, distilled: "Decision: new distilled content.");

        var summary = await RunAsync(dryRun: true, capture);

        Assert.Equal("old raw content", Assert.Single(_memoryRepo.Memories.Values).Content);
        Assert.Equal("update", Assert.Single(summary.Results).Action);
    }

    [Fact]
    public async Task Capture_Without_A_Memory_That_Now_Passes_Is_Created()
    {
        var capture = AddCapture(1);
        PipelineYields(score: 3.5, distilled: "Decision: use pgvector.");

        var summary = await RunAsync(dryRun: false, capture);

        Assert.Single(_memoryRepo.Memories);
        Assert.Equal("create", Assert.Single(summary.Results).Action);
    }

    [Fact]
    public async Task Unchanged_Memory_Produces_No_Change()
    {
        var capture = AddCapture(1);
        AddMemory(1, "Decision: use pgvector.", score: 3.5, category: "decision");
        PipelineYields(score: 3.5, distilled: "Decision: use pgvector.");

        var summary = await RunAsync(dryRun: false, capture);

        Assert.Empty(summary.Results);
        Assert.Single(_memoryRepo.Memories);
    }

    [Fact]
    public async Task Reprocessed_Memory_Is_Deduplicated_Against_Other_Memories()
    {
        // Memory A (capture 10) already says the same thing; capture 11's old memory is stale.
        AddCapture(10);
        var survivor = AddMemory(10, "Decision: use pgvector with HNSW.", score: 3.0);
        var capture = AddCapture(11);
        AddMemory(11, "older duplicate of the pgvector decision", score: 2.0);
        PipelineYields(score: 3.8, distilled: "Decision: use pgvector with HNSW.");
        _memoryRepo.StubSimilarity = 1.0;   // everything in the store looks identical

        var summary = await RunAsync(dryRun: false, capture);

        // Old row for capture 11 removed, new one merged into the survivor rather than inserted.
        var only = Assert.Single(_memoryRepo.Memories.Values);
        Assert.Equal(survivor.Id, only.Id);
        Assert.Equal(2, only.SeenCount);
        Assert.Equal(3.8, only.Score);
        Assert.Equal("deduplicated", Assert.Single(summary.Results).Outcome);
    }

    // ── Failures must never be mistaken for "no longer worth remembering" ──────────

    [Fact]
    public async Task Classifier_Failure_Keeps_The_Old_Memory_And_Reports_An_Error()
    {
        var capture = AddCapture(1);
        AddMemory(1, "Decision: a good memory that must survive an API hiccup.");
        _classification
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("unknown", 0.0, "HTTP 429 rate limited"));

        var summary = await RunAsync(dryRun: false, capture);

        Assert.Equal("Decision: a good memory that must survive an API hiccup.",
            Assert.Single(_memoryRepo.Memories.Values).Content);
        var item = Assert.Single(summary.Results);
        Assert.Equal("error", item.Action);
        Assert.Equal("kept_old", item.Outcome);
        Assert.Contains("429", item.Reason);
        Assert.Equal(1, summary.TotalErrors);
        Assert.Equal(0, summary.TotalChanges);
        Assert.Equal("reprocessed_with_errors", summary.Status);
    }

    [Fact]
    public async Task Distiller_Failure_Keeps_The_Old_Memory()
    {
        var capture = AddCapture(1);
        AddMemory(1, "old memory");
        _classification
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("decision", 4.0, null));
        _distillation
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult(null, "Distillation timed out"));

        var summary = await RunAsync(dryRun: false, capture);

        Assert.Single(_memoryRepo.Memories);
        Assert.Equal("error", Assert.Single(summary.Results).Action);
    }

    [Fact]
    public async Task Embedding_Failure_Keeps_The_Old_Memory()
    {
        var capture = AddCapture(1);
        AddMemory(1, "old memory");
        PipelineYields(score: 4.0, distilled: "Decision: new content.");
        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((float[]?)null);

        var summary = await RunAsync(dryRun: false, capture);

        Assert.Equal("old memory", Assert.Single(_memoryRepo.Memories.Values).Content);
        Assert.Equal("error", Assert.Single(summary.Results).Action);
    }

    [Fact]
    public async Task A_Skip_Decision_Is_Not_Reported_As_An_Error()
    {
        var capture = AddCapture(1);
        AddMemory(1, "stale");
        PipelineYields(score: 0.0, distilled: null);

        var summary = await RunAsync(dryRun: true, capture);

        Assert.Equal(0, summary.TotalErrors);
        var item = Assert.Single(summary.Results);
        Assert.Equal("delete_old_memory", item.Action);
        Assert.Contains("below threshold", item.Reason);
    }

    [Fact]
    public async Task Store_Failure_Restores_The_Old_Memory_And_The_Batch_Continues()
    {
        var first = AddCapture(1);
        AddMemory(1, "old memory one", score: 3.0);
        var second = AddCapture(2);
        AddMemory(2, "old memory two", score: 3.0);
        PipelineYields(score: 4.0, distilled: "Decision: replacement content.");
        _memoryRepo.SearchFailuresRemaining = 1;   // the dedup search for capture 1 blows up

        var summary = await RunAsync(dryRun: false, first, second);

        // Capture 1: old memory put back (not lost), error reported.
        var restored = Assert.Single(_memoryRepo.Memories.Values, m => m.CaptureId == 1);
        Assert.Equal("old memory one", restored.Content);
        Assert.Equal(3.0, restored.Score);
        Assert.Contains(summary.Results, r => r.CaptureId == 1 && r.Action == "error");

        // Capture 2 was still processed normally.
        var replaced = Assert.Single(_memoryRepo.Memories.Values, m => m.CaptureId == 2);
        Assert.Equal("Decision: replacement content.", replaced.Content);
        Assert.Contains(summary.Results, r => r.CaptureId == 2 && r.Action == "update" && r.Outcome == "stored");
        Assert.Equal(1, summary.TotalErrors);
        Assert.Equal(1, summary.TotalChanges);
    }

    [Fact]
    public async Task Evaluate_Distinguishes_Skipped_From_Failed()
    {
        var capture = AddCapture(1);

        PipelineYields(score: 0.0, distilled: null);
        var skipped = await _processor.EvaluateCaptureAsync(capture);
        Assert.False(skipped.IsFailure);
        Assert.Null(skipped.Memory);
        Assert.NotNull(skipped.SkipReason);

        _classification
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("unknown", 0.0, "boom"));
        var failed = await _processor.EvaluateCaptureAsync(capture);
        Assert.True(failed.IsFailure);
        Assert.Null(failed.Memory);
    }
}
