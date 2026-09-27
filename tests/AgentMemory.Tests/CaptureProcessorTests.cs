using AgentMemory.Classification;
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
                ["Memory:SaveThreshold"] = "0.5"
            })!;
        _config = configBuilder.Build();

        _processor = new CaptureProcessor(
            _captureRepo,
            _classificationMock.Object,
            _embeddingMock.Object,
            _memoryRepo,
            _config,
            _logger.Object);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Stores_Memory_When_Score_Above_Threshold()
    {
        // Arrange
        var capture = new CaptureRecord
        {
            Id = 1,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "Important architectural decision about data pipeline."
        };
        _captureRepo.Captures[1] = capture;

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("decision", 3.2, null));

        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        // Act
        await _processor.ProcessCaptureAsync(1);

        // Assert
        var memory = Assert.Single(_memoryRepo.Memories.Values);
        Assert.Equal(1, memory.CaptureId);
        Assert.Equal("decision", memory.Category);
        Assert.Equal(3.2, memory.Score);
        Assert.Equal(capture.RawContent, memory.Content);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Skips_Memory_When_Score_Below_Threshold()
    {
        // Arrange
        _captureRepo.Captures[1] = new CaptureRecord
        {
            Id = 1,
            SessionId = "test-session",
            HookEvent = "PostToolUse",
            RawContent = "fix: typo in comment"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("coding", 0.1, null));

        // Act
        await _processor.ProcessCaptureAsync(1);

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
        _captureRepo.Captures[1] = new CaptureRecord
        {
            Id = 1,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "Some content"
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("unknown", 0.0, "API key not configured"));

        // Act (should not throw)
        var exception = await Record.ExceptionAsync(() =>
            _processor.ProcessCaptureAsync(1));

        // Assert
        Assert.Null(exception);
        Assert.Empty(_memoryRepo.Memories);
    }

    [Fact]
    public async Task ProcessCaptureAsync_Handles_Embedding_Failure_Gracefully()
    {
        // Arrange
        _captureRepo.Captures[1] = new CaptureRecord
        {
            Id = 1,
            SessionId = "test-session",
            HookEvent = "UserPromptSubmit",
            RawContent = "Important content that should be remembered."
        };

        _classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("question", 3.0, null));

        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((float[]?)null);

        // Act (should not throw, just skip)
        var exception = await Record.ExceptionAsync(() =>
            _processor.ProcessCaptureAsync(1));

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

    public Task AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default)
    {
        memory.Id = _nextId++;
        Memories[memory.Id] = memory;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemorySearchResult>> SearchMemoriesAsync(
        Pgvector.Vector queryEmbedding,
        int limit = 5,
        double minSimilarity = 0.7,
        string? category = null,
        CancellationToken ct = default)
    {
        var results = Memories.Values
            .Where(m => category == null || m.Category == category)
            .Select(m => new MemorySearchResult(
                m.Id,
                m.Category,
                m.Score,
                m.Content,
                m.CreatedAt,
                1.0  // Stub: always return max similarity for tests
            ))
            .Take(limit)
            .ToList() as IReadOnlyList<MemorySearchResult> ?? Array.Empty<MemorySearchResult>();

        return Task.FromResult(results);
    }
}