using System.Text.Json;
using AgentMemory.Classification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RichardSzalay.MockHttp;

namespace AgentMemory.Tests;

public class ClassificationServiceTests
{
    private readonly MockHttpMessageHandler _mockHttp = new();
    private readonly Mock<IConfiguration> _config = new();
    private readonly Mock<ILogger<ClassificationService>> _logger = new();
    private const string ApiKey = "sk-test-key";
    private const string BaseUrl = "https://openrouter.ai/api";

    public ClassificationServiceTests()
    {
        _config.Setup(c => c["OPENROUTER_API_KEY"]).Returns(ApiKey);
        _config.Setup(c => c["OPENROUTER_BASE_URL"]).Returns(BaseUrl);
    }

    private ClassificationService CreateService()
    {
        var httpClient = _mockHttp.ToHttpClient();
        return new ClassificationService(httpClient, _config.Object, _logger.Object);
    }

    private static string ChatCompletionResponseWith(string payloadJson) => JsonSerializer.Serialize(new
    {
        id = "gen-123",
        model = ClassificationService.DefaultModel,
        choices = new[]
        {
            new { message = new { role = "assistant", content = payloadJson } }
        }
    });

    [Fact]
    public async Task ClassifyAsync_Returns_Category_And_Score_On_Success()
    {
        // Arrange
        var payload = JsonSerializer.Serialize(new { category = "decision", save_worthiness = 2.5 });

        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            .Respond("application/json", ChatCompletionResponseWith(payload));

        var service = CreateService();

        // Act
        var result = await service.ClassifyAsync("We decided to use pgvector for embeddings.");

        // Assert
        Assert.Null(result.Error);
        Assert.Equal("decision", result.Category);
        Assert.Equal(2.5, result.SaveWorthiness);
    }

    [Fact]
    public async Task ClassifyAsync_Returns_Unknown_On_Empty_Response()
    {
        // Arrange
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            .Respond("application/json", "{}");

        var service = CreateService();

        // Act
        var result = await service.ClassifyAsync("Some content");

        // Assert
        Assert.Equal("unknown", result.Category);
        Assert.Equal(0.0, result.SaveWorthiness);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_Returns_Unknown_On_Unparseable_Content()
    {
        // Arrange
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            .Respond("application/json", ChatCompletionResponseWith("not json"));

        var service = CreateService();

        // Act
        var result = await service.ClassifyAsync("Some content");

        // Assert
        Assert.Equal("unknown", result.Category);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_Returns_Error_On_Missing_ApiKey()
    {
        // Arrange
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["OPENROUTER_API_KEY"]).Returns((string?)null);
        config.Setup(c => c["OPENROUTER_BASE_URL"]).Returns(BaseUrl);

        var httpClient = _mockHttp.ToHttpClient();
        var service = new ClassificationService(httpClient, config.Object, _logger.Object);

        // Act
        var result = await service.ClassifyAsync("Some content");

        // Assert
        Assert.Equal("unknown", result.Category);
        Assert.Equal(0.0, result.SaveWorthiness);
        Assert.Contains("not configured", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_Returns_Error_On_Http_Error()
    {
        // Arrange
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            .Respond(System.Net.HttpStatusCode.InternalServerError);

        var service = CreateService();

        // Act
        var result = await service.ClassifyAsync("Some content");

        // Assert
        Assert.Equal("unknown", result.Category);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_Uses_Configured_Model_Override()
    {
        // Arrange
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["OPENROUTER_API_KEY"]).Returns(ApiKey);
        config.Setup(c => c["OPENROUTER_BASE_URL"]).Returns(BaseUrl);
        config.Setup(c => c["OPENROUTER_CLASSIFICATION_MODEL"]).Returns("anthropic/claude-haiku-4.5");

        var payload = JsonSerializer.Serialize(new { category = "coding", save_worthiness = 1.0 });
        var capturedRequestBody = string.Empty;

        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
            .With(req =>
            {
                capturedRequestBody = req.Content!.ReadAsStringAsync().Result;
                return true;
            })
            .Respond("application/json", ChatCompletionResponseWith(payload));

        var httpClient = _mockHttp.ToHttpClient();
        var service = new ClassificationService(httpClient, config.Object, _logger.Object);

        // Act
        await service.ClassifyAsync("Some content");

        // Assert
        Assert.Contains("anthropic/claude-haiku-4.5", capturedRequestBody);
    }
}
