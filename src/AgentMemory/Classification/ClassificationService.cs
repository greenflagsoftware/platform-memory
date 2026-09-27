using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Classification;

/// <summary>
/// Classifies captured events with a single OpenRouter chat completion: a JSON-mode
/// prompt asks for both a category label and a save-worthiness score (0-4) in one call.
/// Model and base URL are configurable — see appsettings.json's "OpenRouter" section
/// or the OPENROUTER_* environment variables.
/// </summary>
public class ClassificationService : IClassificationService
{
    public const string DefaultModel = "openai/gpt-4o-mini";
    private const string DefaultBaseUrl = "https://openrouter.ai/api";

    private static readonly string[] Categories =
        ["question", "coding", "tool_call", "decision", "configuration", "other"];

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly ILogger<ClassificationService> _logger;

    public ClassificationService(
        HttpClient http,
        IConfiguration configuration,
        ILogger<ClassificationService> logger)
    {
        _http = http;
        _apiKey = configuration["OPENROUTER_API_KEY"]
                  ?? configuration["OpenRouter:ApiKey"]
                  ?? string.Empty;
        _baseUrl = configuration["OPENROUTER_BASE_URL"]
                   ?? configuration["OpenRouter:BaseUrl"]
                   ?? DefaultBaseUrl;
        _model = configuration["OPENROUTER_CLASSIFICATION_MODEL"]
                 ?? configuration["OpenRouter:ClassificationModel"]
                 ?? DefaultModel;
        _logger = logger;
    }

    public async Task<ClassificationResult> ClassifyAsync(string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new ClassificationResult("unknown", 0.0, "OPENROUTER_API_KEY not configured");
        }

        try
        {
            var request = new
            {
                model = _model,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "capture_classification",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                category = new { type = "string", @enum = Categories },
                                save_worthiness = new
                                {
                                    type = "number",
                                    description = "0 = not worth saving, 4 = essential to remember across sessions"
                                }
                            },
                            required = new[] { "category", "save_worthiness" },
                            additionalProperties = false
                        }
                    }
                },
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You classify one captured event from a coding-agent session. " +
                                  "Respond with a category — one of: " + string.Join(", ", Categories) +
                                  " — and a save_worthiness score from 0 to 4 (0 = trivial/ephemeral, " +
                                  "4 = essential project knowledge that would save significant time in a " +
                                  "future session). Consider whether the content reveals project " +
                                  "conventions, architectural decisions, or non-obvious gotchas."
                    },
                    new { role = "user", content }
                }
            };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await _http.SendAsync(httpRequest, cts.Token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cts.Token);

            var messageContent = result?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(messageContent))
            {
                return new ClassificationResult("unknown", 0.0, "Empty response from OpenRouter");
            }

            var parsed = JsonSerializer.Deserialize<ClassificationPayload>(
                messageContent,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });

            if (parsed == null)
            {
                return new ClassificationResult("unknown", 0.0, "Could not parse classification JSON");
            }

            var category = string.IsNullOrWhiteSpace(parsed.Category) ? "unknown" : parsed.Category;

            _logger.LogInformation(
                "Classified capture: category={Category}, score={Score:F2}",
                category, parsed.SaveWorthiness);

            return new ClassificationResult(category, parsed.SaveWorthiness, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Classification timed out after 30s");
            return new ClassificationResult("unknown", 0.0, "Classification timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Classification failed");
            return new ClassificationResult("unknown", 0.0, $"Classification error: {ex.Message}");
        }
    }
}

internal class ChatCompletionResponse
{
    public ChatChoice[]? Choices { get; set; }
}

internal class ChatChoice
{
    public ChatMessage? Message { get; set; }
}

internal class ChatMessage
{
    public string? Content { get; set; }
}

internal class ClassificationPayload
{
    public string Category { get; set; } = string.Empty;
    public double SaveWorthiness { get; set; }
}
