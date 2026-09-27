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
        _config.Setup(c => c["TYPESAFE_BASE_URL"]).Returns(BaseUrl);
    }

    private ClassificationService CreateService()
    {
        var httpClient = _mockHttp.ToHttpClient();
        return new ClassificationService(httpClient, _config.Object, _logger.Object);
    }

    [Fact]
    public async Task ClassifyAsync_Returns_Category_And_Score_On_Success()
    {
        // Arrange
        var responseBody = new
        {
            model = "jev-1.13.0",
            answers = new
            {
                category = new
                {
                    type = "choice",
                    choice = "decision",
                    probabilities = new { decision = 0.85, question = 0.10, coding = 0.05 },
                    confidence = 0.80
                },
                save_worthiness = new
                {
                    type = "score",
                    score = 2.5,
                    legend = new[] { "Not worth saving", "Slightly useful", "Moderately useful", "Very useful", "Essential" },
                    probabilities = new { _0 = 0.0, _1 = 0.0, _2 = 0.5, _3 = 0.5, _4 = 0.0 },
                    confidence = 0.90
                }
            },
            usage = new { input_tokens = 300, output_tokens = 20 }
        };

        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/systemone")
            .Respond("application/json", JsonSerializer.Serialize(responseBody));

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
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/systemone")
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
    public async Task ClassifyAsync_Returns_Unknown_On_No_Answers()
    {
        // Arrange
        var responseBody = new { model = "jev-1.13.0", answers = (object?)null };
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/systemone")
            .Respond("application/json", JsonSerializer.Serialize(responseBody));

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
        config.Setup(c => c["TYPESAFE_BASE_URL"]).Returns(BaseUrl);

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
        _mockHttp.Expect(HttpMethod.Post, $"{BaseUrl}/v1/systemone")
            .Respond(System.Net.HttpStatusCode.InternalServerError);

        var service = CreateService();

        // Act
        var result = await service.ClassifyAsync("Some content");

        // Assert
        Assert.Equal("unknown", result.Category);
        Assert.NotNull(result.Error);
    }
}