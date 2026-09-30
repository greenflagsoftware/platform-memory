using AgentMemory.Capture;
using AgentMemory.Retrieval;
using AgentMemory.Storage;
using Moq;

namespace AgentMemory.Tests;

public class McpToolsTests
{
    private readonly Mock<IRetrievalService> _retrievalMock = new();
    private readonly McpTools _tool;

    public McpToolsTests()
    {
        _tool = new McpTools(_retrievalMock.Object);
    }

    [Fact]
    public async Task SearchMemoriesAsync_Maps_Results_And_Applies_Defaults()
    {
        // Arrange
        var expected = new List<MemorySearchResult>
        {
            new(1, "decision", 4.0, "We decided to use pgvector.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0.91),
        };

        _retrievalMock
            .Setup(r => r.SearchAsync(
                "why pgvector",
                5,
                0.7,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        // Act
        var result = await _tool.SearchMemoriesAsync("why pgvector");

        // Assert — defaults (limit=5, min_similarity=0.7, category=null) are forwarded as-is
        _retrievalMock.Verify(r => r.SearchAsync("why pgvector", 5, 0.7, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("why pgvector", result.Query);
        Assert.Equal(1, result.ResultCount);
        var item = Assert.Single(result.Results);
        Assert.Equal(1, item.Id);
        Assert.Equal("decision", item.Category);
        Assert.Equal(4.0, item.Score);
        Assert.Equal(0.91, item.Similarity);
    }

    [Fact]
    public async Task SearchMemoriesAsync_Forwards_Explicit_Overrides()
    {
        // Arrange
        _retrievalMock
            .Setup(r => r.SearchAsync(
                "config setup",
                10,
                0.5,
                "configuration",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemorySearchResult>());

        // Act
        var result = await _tool.SearchMemoriesAsync(
            "config setup",
            limit: 10,
            min_similarity: 0.5,
            category: "configuration");

        // Assert
        _retrievalMock.Verify(r => r.SearchAsync("config setup", 10, 0.5, "configuration", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, result.ResultCount);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task SearchMemoriesAsync_Returns_Empty_When_No_Matches()
    {
        // Arrange
        _retrievalMock
            .Setup(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemorySearchResult>());

        // Act
        var result = await _tool.SearchMemoriesAsync("nothing relevant");

        // Assert
        Assert.Equal(0, result.ResultCount);
        Assert.Empty(result.Results);
    }
}
