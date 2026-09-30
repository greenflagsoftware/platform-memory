using System.Text.Json;
using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using AgentMemory.Processing;
using AgentMemory.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentMemory.Tests;

public class CaptureProcessorTests
{
    private readonly IConfiguration _config;
    private readonly Mock<ILogger<CaptureProcessor>> _logger = new();
    private readonly Mock<IClassificationService> _classificationMock = new();
    private readonly Mock<IDistillationService> _distillationMock = new();
    private readonly Mock<IEmbeddingService> _embeddingMock = new();
    private readonly InMemoryCaptureRepository _captureRepo = new();
    private readonly InMemoryMemoryRepository _memoryRepo = new();
    private readonly CaptureProcessor _processor;

    public CaptureProcessorTests()
    {
        // Use a real IConfiguration with the threshold value
        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:SaveThreshold"] = "2.5"
            })!;
        _config = configBuilder.Build();

        _processor = new CaptureProcessor(
            _captureRepo,
            _classificationMock.Object,
            _distillationMock.Object,
            _embeddingMock.Object,
            _memoryRepo,
            _config,
            _logger.Object);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Stores_Distilled_Content_And_SourceExcerpt()
    {
        // Arrange
        var capture = new CaptureRecord
        {
            Id = 1,
            SessionId = "test-session",
            HookEvent = "PostToolUse",
            RawContent = "Bash: git commit -m 'fix: typo' -> success"
        };
        _captureRepo.Captures[1] = capture;

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("decision", 3.2, null));

        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult("Decision: committed the typo fix to main."));

        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        // Act
        await _processor.ProcessCaptureAsync(1);

        // Assert — content is the distilled fact, not the raw capture
        var memory = Assert.Single(_memoryRepo.Memories.Values);
        Assert.Equal(1, memory.CaptureId);
        Assert.Equal("decision", memory.Category);
        Assert.Equal(3.2, memory.Score);
        Assert.Equal("Decision: committed the typo fix to main.", memory.Content);
        Assert.Equal(capture.RawContent, memory.SourceExcerpt);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Stores_Nothing_When_Distillation_Returns_NONE()
    {
        // Arrange
        _captureRepo.Captures[2] = new CaptureRecord
        {
            Id = 2,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "We decided to use pgvector for all embeddings."
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("decision", 3.0, null));

        // NONE (null content, no error): the distiller found no durable fact.
        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult(null));

        // Act
        await _processor.ProcessCaptureAsync(2);

        // Assert — must NOT fall back to storing the raw capture, and must not even embed it
        Assert.Empty(_memoryRepo.Memories);
        _embeddingMock.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private async Task<string?> DistillerContextForAsync(string? metadata)
    {
        _captureRepo.Captures[7] = new CaptureRecord
        {
            Id = 7,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "Yes please commit and push the hook fixes.",
            Metadata = metadata ?? "{}"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("decision", 3.0, null));

        string? received = "<distiller not called>";
        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((_, ctx, _) => received = ctx)
            .ReturnsAsync(new DistillationResult(null));   // NONE: we only care what it was given

        await _processor.ProcessCaptureAsync(7);
        return received;
    }

    [Fact]
    public async Task Distiller_Receives_Hook_Context_From_Metadata()
    {
        var metadata = JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-30T10:00:00Z",
            context = "User: Should I commit?\nAssistant: The fixes are ready. Commit and push?"
        });

        var context = await DistillerContextForAsync(metadata);

        Assert.Equal("User: Should I commit?\nAssistant: The fixes are ready. Commit and push?", context);
    }

    [Fact]
    public async Task Distiller_Context_Is_Capped_At_2000_Chars()
    {
        var metadata = JsonSerializer.Serialize(new { context = new string('x', 5000) });

        var context = await DistillerContextForAsync(metadata);

        Assert.Equal(2000, context!.Length);
    }

    [Fact]
    public async Task Distiller_Falls_Back_To_PreviousTurns_Metadata_Key()
    {
        var context = await DistillerContextForAsync(
            JsonSerializer.Serialize(new { previous_turns = "User: earlier message" }));

        Assert.Equal("User: earlier message", context);
    }

    [Theory]
    [InlineData(null)]                          // no metadata
    [InlineData("{}")]
    [InlineData("{\"context\":null}")]          // hooks send null when no transcript is available
    [InlineData("{\"context\":42}")]            // wrong type
    [InlineData("this is not json")]            // malformed
    public async Task Distiller_Gets_Null_Context_When_Metadata_Has_None(string? metadata)
    {
        // The capture must still be processed (distiller reached), just without context.
        Assert.Null(await DistillerContextForAsync(metadata));
    }

    [Fact]
    public async Task ProcessCaptureAsync_Skips_When_Distillation_Errors()
    {
        // Arrange
        _captureRepo.Captures[3] = new CaptureRecord
        {
            Id = 3,
            SessionId = "test-session",
            HookEvent = "PostToolUse",
            RawContent = "Some content worth remembering"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("coding", 2.6, null));

        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult(null, "API error"));

        // Act
        await _processor.ProcessCaptureAsync(3);

        // Assert — reached distillation (gate passed), no memory stored because it errored
        _distillationMock.Verify(
            d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Empty(_memoryRepo.Memories);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Skips_Memory_When_Score_Below_Threshold()
    {
        // Arrange
        _captureRepo.Captures[4] = new CaptureRecord
        {
            Id = 4,
            SessionId = "test-session",
            HookEvent = "PostToolUse",
            RawContent = "fix: typo in comment"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("coding", 0.1, null));

        // Act
        await _processor.ProcessCaptureAsync(4);

        // Assert
        Assert.Empty(_memoryRepo.Memories);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Does_Not_Throw_When_Capture_Not_Found()
    {
        // Act (should not throw)
        var exception = await Record.ExceptionAsync(() =>
            _processor.ProcessCaptureAsync(999));

        // Assert
        Assert.Null(exception);
        Assert.Empty(_memoryRepo.Memories);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Handles_Classification_Error_Gracefully()
    {
        // Arrange
        _captureRepo.Captures[5] = new CaptureRecord
        {
            Id = 5,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "Some content worth remembering"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("unknown", 0.0, "API key not configured"));

        // Act (should not throw)
        var exception = await Record.ExceptionAsync(() =>
            _processor.ProcessCaptureAsync(5));

        // Assert — reached classification (gate passed), nothing stored
        Assert.Null(exception);
        _classificationMock.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Empty(_memoryRepo.Memories);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Handles_Embedding_Failure_Gracefully()
    {
        // Arrange
        _captureRepo.Captures[6] = new CaptureRecord
        {
            Id = 6,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "Important content that should be remembered."
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("question", 3.0, null));

        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult("Distilled: remembered content."));

        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((float[]?)null);

        // Act (should not throw, just skip)
        var exception = await Record.ExceptionAsync(() =>
            _processor.ProcessCaptureAsync(6));

        // Assert
        Assert.Null(exception);
        Assert.Empty(_memoryRepo.Memories);
    }
}

/// <summary>
/// Simple in-memory capture repository for tests.
/// </summary>
public class InMemoryCaptureRepository : ICaptureRepository
{
    public Dictionary<long, CaptureRecord> Captures { get; } = new();

    public Task<CaptureRecord?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        Captures.TryGetValue(id, out var capture);
        return Task.FromResult(capture);
    }
}

/// <summary>
/// Simple in-memory memory repository for tests.
/// </summary>
public class InMemoryMemoryRepository : IMemoryRepository
{
    public Dictionary<long, MemoryRecord> Memories { get; } = new();
    private long _nextId = 1;

    /// <summary>
    /// Similarity reported for every stored memory by <see cref="SearchMemoriesAsync"/>.
    /// Default 0 means "nothing is similar" (no dedup); set to 1 to force a dedup hit.
    /// </summary>
    public double StubSimilarity { get; set; } = 0.0;

    /// <summary>Number of upcoming <see cref="SearchMemoriesAsync"/> calls that throw (simulates a DB error).</summary>
    public int SearchFailuresRemaining { get; set; }

    public Task<MemoryRecord?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        Memories.TryGetValue(id, out var memory);
        return Task.FromResult(memory);
    }

    public Task<IReadOnlyList<MemoryRecord>> GetByCaptureIdAsync(long captureId, CancellationToken ct = default)
    {
        IReadOnlyList<MemoryRecord> list = Memories.Values
            .Where(m => m.CaptureId == captureId)
            .OrderByDescending(m => m.CreatedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task DeleteMemoriesAsync(IEnumerable<MemoryRecord> memories, CancellationToken ct = default)
    {
        foreach (var m in memories.ToList())
        {
            Memories.Remove(m.Id);
        }
        return Task.CompletedTask;
    }

    public Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        memory.Id = _nextId++;
        Memories[memory.Id] = memory;
        return Task.CompletedTask;
    }

    public Task UpdateMemoryAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        if (Memories.ContainsKey(memory.Id))
        {
            Memories[memory.Id] = memory;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemorySearchResult>> SearchMemoriesAsync(
        Pgvector.Vector queryEmbedding,
        int limit = 5,
        double minSimilarity = 0.7,
        string? category = null,
        CancellationToken ct = default)
    {
        if (SearchFailuresRemaining > 0)
        {
            SearchFailuresRemaining--;
            throw new InvalidOperationException("Connection already open (simulated)");
        }

        var stubSimilarity = StubSimilarity;
        var results = Memories.Values
            .Where(m => (category == null || m.Category == category)
                        && stubSimilarity >= minSimilarity)
            .Select(m => new MemorySearchResult(
                m.Id,
                m.Category,
                m.Score,
                m.Content,
                m.CreatedAt,
                m.LastSeenAt,
                m.SeenCount,
                stubSimilarity,
                m.SourceExcerpt
            ))
            .Take(limit)
            .ToList() as IReadOnlyList<MemorySearchResult> ?? Array.Empty<MemorySearchResult>();

        return Task.FromResult(results);
    }
}