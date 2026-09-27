using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Classification;

/// <summary>
/// Result of a JEV classification call for a captured event.
/// </summary>
public record ClassificationResult(
    string Category,
    double SaveWorthiness,
    string? Error
);

/// <summary>
/// Classifies captured events using JEV (via the OpenRouter-hosted TypeSafe API).
/// Makes a single API call with two parallel questions:
///   - Choice: which category best describes this event
///   - Score: how valuable is this information for future sessions
/// </summary>
public class ClassificationService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
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
        _baseUrl = configuration["TYPESAFE_BASE_URL"]
                   ?? "https://openrouter.ai/api";
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
                state = content,
                model = "jev-latest",
                questions = new
                {
                    category = new
                    {
                        type = "choice",
                        instructions = "What category best describes this capture event from a coding-agent session?",
                        criteria = new
                        {
                            question = "The user is asking a question about the codebase, architecture, or process",
                            coding = "The agent is writing, editing, or debugging code",
                            tool_call = "The agent is calling a tool (Read, Glob, Grep, etc.) to explore the codebase",
                            decision = "A design decision, preference, or rationale was expressed",
                            configuration = "Configuration, dependency, or environment setup",
                            other = "None of the above categories fit"
                        }
                    },
                    save_worthiness = new
                    {
                        type = "score",
                        instructions = "How valuable is this information for future coding-agent sessions in the same project? Consider whether it reveals project conventions, architectural decisions, non-obvious gotchas, or important context that would help a future session be more effective.",
                        criteria = new[]
                        {
                            "Not worth saving — trivial or ephemeral",
                            "Slightly useful — minor context, easy to rediscover",
                            "Moderately useful — decent project insight, worth indexing",
                            "Very useful — important project knowledge, would save significant time",
                            "Essential — critical project context, must remember across sessions"
                        }
                    }
                }
            };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/systemone")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await _http.SendAsync(httpRequest, cts.Token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JevResponse>(
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
                },
                cts.Token);

            if (result?.Answers == null)
            {
                return new ClassificationResult("unknown", 0.0, "Empty response from JEV API");
            }

            var category = result.Answers.Category?.Choice ?? "unknown";
            var score = result.Answers.SaveWorthiness?.Score ?? 0.0;

            _logger.LogInformation(
                "Classified capture: category={Category}, score={Score:F2}",
                category, score);

            return new ClassificationResult(category, score, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("JEV classification timed out after 30s");
            return new ClassificationResult("unknown", 0.0, "Classification timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JEV classification failed");
            return new ClassificationResult("unknown", 0.0, $"Classification error: {ex.Message}");
        }
    }
}

#pragma warning disable IDE1006 // Naming styles — JEV API uses snake_case
internal class JevResponse
{
    public JevAnswers? Answers { get; set; }
}

internal class JevAnswers
{
    public JevChoiceAnswer? Category { get; set; }
    public JevScoreAnswer? SaveWorthiness { get; set; }
}

internal class JevChoiceAnswer
{
    public string Type { get; set; } = string.Empty;
    public string Choice { get; set; } = string.Empty;
}

internal class JevScoreAnswer
{
    public string Type { get; set; } = string.Empty;
    public double Score { get; set; }
}
#pragma warning restore IDE1006