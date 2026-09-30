using AgentMemory.Embedding;
using AgentMemory.Retrieval;
using AgentMemory.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentMemory.Tests;

public class RetrievalServiceTests
{
    private readonly Mock<IEmbeddingService> _embeddingMock = new();
    private readonly Mock<IMemoryRepository> _memoryRepoMock = new();
    private readonly Mock<ILogger<RetrievalService>> _logger = new();
    private readonly RetrievalService _service;

    public RetrievalServiceTests()
    {
        _service = new RetrievalService(
            _embeddingMock.Object,
            _memoryRepoMock.Object,
            _logger.Object);
    }

    [Fact]
    public async Task SearchAsync_Returns_Results_On_Success()
    {
        // Arrange
        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync("test query", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        var expected = new List<MemorySearchResult>
        {
            new(1, "decision", 3.5, "Important decision", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0.92),
            new(2, "coding", 2.0, "Code pattern", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0.85),
        };

        _memoryRepoMock
            .Setup(r => r.SearchMemoriesAsync(
                It.IsAny<Pgvector.Vector>(),
                5, 0.7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        // Act
        var results = await _service.SearchAsync("test query");

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Id);
        Assert.Equal("decision", results[0].Category);
        Assert.Equal(0.92, results[0].Similarity);
    }

    [Fact]
    public async Task SearchAsync_Returns_Empty_On_Empty_Query()
    {
        // Act
        var results = await _service.SearchAsync("");

        // Assert
        Assert.Empty(results);
        _embeddingMock.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_Returns_Empty_On_Whitespace_Query()
    {
        // Act
        var results = await _service.SearchAsync("   ");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_Returns_Empty_When_Embedding_Fails()
    {
        // Arrange
        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((float[]?)null);

        // Act
        var results = await _service.SearchAsync("test query");

        // Assert
        Assert.Empty(results);
        _memoryRepoMock.Verify(
            r => r.SearchMemoriesAsync(It.IsAny<Pgvector.Vector>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_Returns_Empty_When_Embedding_Is_Empty()
    {
        // Arrange
        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<float>());

        // Act
        var results = await _service.SearchAsync("test query");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_Passes_Limit_And_Similarity_And_Category()
    {
        // Arrange
        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        _memoryRepoMock
            .Setup(r => r.SearchMemoriesAsync(
                It.IsAny<Pgvector.Vector>(),
                3, 0.8, "decision", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemorySearchResult>
            {
                new(1, "decision", 3.5, "Important decision", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0.91)
            });

        // Act
        var results = await _service.SearchAsync("test query", limit: 3, minSimilarity: 0.8, category: "decision");

        // Assert
        Assert.Single(results);
        Assert.Equal("decision", results[0].Category);
    }

    [Fact]
    public async Task SearchAsync_Handles_Embedding_Exception_Gracefully()
    {
        // Arrange
        _embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var results = await _service.SearchAsync("test query");

        // Assert
        Assert.Empty(results);
    }
}