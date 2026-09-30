using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Classification;

/// <summary>
/// Classifies captured events with a single OpenRouter chat completion: a JSON-mode
/// prompt asks for both a category label and a save-worthiness score (0-4) in one call.
/// Model and base URL are configurable — see appsettings.json's "OpenRouter" section
/// or the OPENROUTER_* environment variables.
///
/// Quality plan phases implemented here:
/// - Phase 2b: Rewritten system prompt with few-shot anchors and explicit guidance
/// - Phase 2c: Constrained schema (minimum 0, maximum 4 on save_worthiness) + reason field
/// </summary>
public class ClassificationService : IClassificationService
{
    public const string DefaultModel = "openai/gpt-4o-mini";
    private const string DefaultBaseUrl = "https://openrouter.ai/api";

    private static readonly string[] Categories =
        ["question", "coding", "tool_call", "decision", "configuration", "other"];

    /// <summary>
    /// System prompt rewritten for Phase 2b with few-shot anchors.
    /// Explicitly anchors scoring to "would this help a future session that has no
    /// memory of this one?" and provides concrete examples for each score level.
    /// </summary>
    private static readonly string SystemPrompt =
        "You classify one captured event from a coding-agent session. " +
        "Your job: decide whether this event contains information that would help " +
        "a future session that has NO memory of this one.\n\n" +
        "Score each event on this 4-point scale:\n" +
        "  4 = ESSENTIAL — architectural decision, project convention, non-obvious gotcha, " +
        "or configuration that would save significant time in a future session.\n" +
        "  2 = USEFUL HOW-TO — demonstrates a workflow, fix, or process worth remembering.\n" +
        "  1 = MARGINAL — technically has information but it's context-dependent or likely " +
        "to be rediscovered easily.\n" +
        "  0 = NOT WORTH SAVING — command log, bare acknowledgement (\"Yes, commit and push.\"), " +
        "test probe, retrieval of existing memories, file read, grep, code browse, " +
        "or any single-turn reply without durable facts.\n\n" +
        "Examples:\n" +
        "  score=4: \"We decided to use pgvector with HNSW indexes for semantic search " +
        "because cosine distance queries on 1536-d vectors need sub-100ms latency.\"\n" +
        "  score=2: \"fix: the Dockerfile was missing the dotnet restore layer — added it " +
        "between the copy and build steps to fix the build failure\"\n" +
        "  score=1: \"The config file is at src/AgentMemory/appsettings.json\"\n" +
        "  score=0: \"Bash: ls -la -> @{type=text; file=}\"\n" +
        "  score=0: \"Yes, commit and push.\"\n" +
        "  score=0: \"Read: Program.cs -> (file contents)\"\n" +
        "  score=0: \"Glob: **/*.cs -> (results)\"\n\n" +
        "Category must be one of: question, coding, tool_call, decision, configuration, other.\n" +
        "Also provide a short 'reason' string explaining the score (this is logged for " +
        "debugging, not stored as a memory).";

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
                                    description = "0 = not worth saving, 4 = essential to remember across sessions",
                                    // Phase 2c: Constrain the range
                                    minimum = 0,
                                    maximum = 4
                                },
                                reason = new
                                {
                                    type = "string",
                                    description = "Short explanation of the score (for debugging, not stored)"
                                }
                            },
                            required = new[] { "category", "save_worthiness", "reason" },
                            additionalProperties = false
                        }
                    }
                },
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = SystemPrompt
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
                "Classified capture: category={Category}, score={Score:F2}, reason={Reason}",
                category, parsed.SaveWorthiness, parsed.Reason ?? "none");

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
    /// <summary>
    /// Phase 2c: Reason field for debuggable tuning.
    /// </summary>
    public string? Reason { get; set; }
}