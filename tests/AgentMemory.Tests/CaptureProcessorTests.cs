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
    public async Task ProcessCaptureAsync_Uses_RawContent_When_Distillation_Returns_NONE()
    {
        // Arrange
        var capture = new CaptureRecord
        {
            Id = 2,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "We decided to use pgvector for all embeddings."
        };
        _captureRepo.Captures[2] = capture;

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("decision", 3.0, null));

        // Distillation returns NONE (null content) — fall back to raw content
        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult(null));

        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        // Act
        await _processor.ProcessCaptureAsync(2);

        // Assert — content is the raw text, no source excerpt
        var memory = Assert.Single(_memoryRepo.Memories.Values);
        Assert.Equal(capture.RawContent, memory.Content);
        Assert.Null(memory.SourceExcerpt);
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
            RawContent = "Some content"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("coding", 2.6, null));

        _distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistillationResult(null, "API error"));

        // Act
        await _processor.ProcessCaptureAsync(3);

        // Assert — no memory stored because distillation errored
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
            RawContent = "Some content"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("unknown", 0.0, "API key not configured"));

        // Act (should not throw)
        var exception = await Record.ExceptionAsync(() =>
            _processor.ProcessCaptureAsync(5));

        // Assert
        Assert.Null(exception);
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

    public Task<MemoryRecord?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        Memories.TryGetValue(id, out var memory);
        return Task.FromResult(memory);
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
        const double stubSimilarity = 1.0;
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