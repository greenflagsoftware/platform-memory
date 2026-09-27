using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Embedding;

/// <summary>
/// Generates embeddings via OpenRouter's embedding endpoint.
/// Uses text-embedding-3-small (1536 dimensions) by default.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    public const string DefaultModel = "openai/text-embedding-3-small";
    private const string DefaultBaseUrl = "https://openrouter.ai/api";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        HttpClient http,
        IConfiguration configuration,
        ILogger<EmbeddingService> logger)
    {
        _http = http;
        _apiKey = configuration["OPENROUTER_API_KEY"]
                  ?? configuration["OpenRouter:ApiKey"]
                  ?? string.Empty;
        _baseUrl = configuration["OPENROUTER_BASE_URL"]
                   ?? configuration["OpenRouter:BaseUrl"]
                   ?? DefaultBaseUrl;
        _model = configuration["OPENROUTER_EMBEDDING_MODEL"]
                 ?? configuration["OpenRouter:EmbeddingModel"]
                 ?? DefaultModel;
        _logger = logger;
    }

    public async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("OPENROUTER_API_KEY not configured; cannot generate embeddings");
            return null;
        }

        try
        {
            var request = new
            {
                model = _model,
                input = text
            };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/embeddings")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await _http.SendAsync(httpRequest, cts.Token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cts.Token);

            if (result?.Data == null || result.Data.Length == 0)
            {
                _logger.LogWarning("Embedding API returned empty data");
                return null;
            }

            return result.Data[0].Embedding;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Embedding request timed out after 30s");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding generation failed");
            return null;
        }
    }
}

#pragma warning disable IDE1006
internal class EmbeddingResponse
{
    public EmbeddingData[]? Data { get; set; }
}

internal class EmbeddingData
{
    public float[]? Embedding { get; set; }
}
#pragma warning restore IDE1006