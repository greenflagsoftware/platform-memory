using System.Text.Json;
using AgentMemory.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RichardSzalay.MockHttp;

namespace AgentMemory.Tests;

public class EmbeddingServiceTests
{
    private readonly MockHttpMessageHandler _mockHttp = new();
    private readonly Mock<IConfiguration> _config = new();
    private readonly Mock<ILogger<EmbeddingService>> _logger = new();
    private const string ApiKey = "sk-test-key";
    private const string BaseUrl = "https://openrouter.ai/api";

    public EmbeddingServiceTests()
    {
        _config.Setup(c => c["OPENROUTER_API_KEY"]).Returns(ApiKey);
        _config.Setup(c => c["TYPESAFE_BASE_URL"]).Returns(BaseUrl);
    }

    private EmbeddingService CreateService()
    {
        var httpClient = _mockHttp.ToHttpClient();
        return new EmbeddingService(httpClient, _config.Object, _logger.Object);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_Returns_Embedding_On_Success()
    {
        // Arrange
        var responseBody = new
        {
            data = new[]
            {
                new { embedding = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f } }
            },
            model = "text-embedding-3-small",
            usage = new { prompt_tokens = 10, total_tokens = 10 }
        };

        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/embeddings")
            .Respond("application/json", JsonSerializer.Serialize(responseBody));

        var service = CreateService();

        // Act
        var result = await service.GenerateEmbeddingAsync("Test text");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Length);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f], result);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_Returns_Null_On_Empty_Data()
    {
        // Arrange
        var responseBody = new { data = Array.Empty<object>() };
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/embeddings")
            .Respond("application/json", JsonSerializer.Serialize(responseBody));

        var service = CreateService();

        // Act
        var result = await service.GenerateEmbeddingAsync("Test text");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_Returns_Null_On_Missing_ApiKey()
    {
        // Arrange
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["OPENROUTER_API_KEY"]).Returns((string?)null);
        config.Setup(c => c["TYPESAFE_BASE_URL"]).Returns(BaseUrl);

        var httpClient = _mockHttp.ToHttpClient();
        var service = new EmbeddingService(httpClient, config.Object, _logger.Object);

        // Act
        var result = await service.GenerateEmbeddingAsync("Test text");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_Returns_Null_On_Http_Error()
    {
        // Arrange
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/embeddings")
            .Respond(System.Net.HttpStatusCode.InternalServerError);

        var service = CreateService();

        // Act
        var result = await service.GenerateEmbeddingAsync("Test text");

        // Assert
        Assert.Null(result);
    }
}