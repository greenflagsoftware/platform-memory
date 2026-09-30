using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Distillation;

/// <summary>
/// Distills raw capture text into a self-contained, standalone fact via an
/// OpenRouter chat completion call.
///
/// Phase 4 of the Memory Quality Plan. The distiller rewrites a capture like
/// "Bash: git commit -m 'fix: typo' -> ..." into a durable fact like
/// "Decision: committed the hook fix to origin/main (commit message was 'fix: typo')".
///
/// When the capture contains no durable fact, returns NONE (DistilledContent = null).
/// </summary>
public class DistillationService : IDistillationService
{
    public const string DefaultModel = "openai/gpt-4o-mini";
    private const string DefaultBaseUrl = "https://openrouter.ai/api";

    private static readonly string SystemPrompt =
        "You extract standalone, durable facts from coding-agent session captures. " +
        "Given a raw capture (a tool call, prompt, or acknowledgment) and optional " +
        "surrounding context, rewrite it into ONE self-contained sentence that would " +
        "make sense to a future session with NO memory of this one.\n\n" +
        "Rules:\n" +
        "  - Restate only what is present in the capture and context. Do not hallucinate.\n" +
        "  - Prepend a category label: 'Decision:', 'Convention:', 'Gotcha:', 'How-to:', " +
        "'Configuration:', or 'Question:' as appropriate.\n" +
        "  - Be specific — include file paths, commit messages, tool names, and values.\n" +
        "  - Output a single line; no bullet points or lists.\n\n" +
        "When the capture contains NO durable fact worth remembering (e.g. bare " +
        "acknowledgement, command log that reveals nothing, file read output, " +
        "retrieval of memories, test probe), respond with exactly: NONE\n\n" +
        "Examples:\n" +
        "  Capture: 'Bash: git commit -m \"fix: add restore layer to Dockerfile\" -> ...'\n" +
        "  Output: Decision: committed a fix that adds the dotnet restore layer to the Dockerfile.\n\n" +
        "  Capture: 'Write: File written to src/appsettings.json: { \"Memory\": { \"SaveThreshold\": 2.5 } }'\n" +
        "  Output: Configuration: set Memory:SaveThreshold to 2.5 in appsettings.json.\n\n" +
        "  Capture: 'Grep: MemorySaveThreshold -> 3 matches'\n" +
        "  Output: NONE\n\n" +
        "  Capture: 'Yes, commit and push.'\n" +
        "  Output: NONE\n\n" +
        "  Capture: 'Read: Program.cs -> (file contents)'\n" +
        "  Output: NONE\n\n" +
        "  Capture: 'ReadLints: ...' (tool output)\n" +
        "  Output: NONE\n\n" +
        "  Capture: 'We should use the CompareExchange pattern for lock-free reads.'\n" +
        "  Output: Decision: use the CompareExchange pattern for lock-free reads (source: session discussion).";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly ILogger<DistillationService> _logger;

    public DistillationService(
        HttpClient http,
        IConfiguration configuration,
        ILogger<DistillationService> logger)
    {
        _http = http;
        _apiKey = configuration["OPENROUTER_API_KEY"]
                  ?? configuration["OpenRouter:ApiKey"]
                  ?? string.Empty;
        _baseUrl = configuration["OPENROUTER_BASE_URL"]
                   ?? configuration["OpenRouter:BaseUrl"]
                   ?? DefaultBaseUrl;
        _model = configuration["OPENROUTER_DISTILLATION_MODEL"]
                 ?? configuration["OpenRouter:DistillationModel"]
                 ?? DefaultModel;
        _logger = logger;
    }

    public async Task<DistillationResult> DistillAsync(
        string rawContent,
        string? context = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new DistillationResult(null, "OPENROUTER_API_KEY not configured");
        }

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return new DistillationResult(null, "Empty raw content");
        }

        try
        {
            var userMessage = rawContent;
            if (!string.IsNullOrWhiteSpace(context))
            {
                userMessage = $"CONTEXT:\n{context}\n\n---\nCAPTURE:\n{rawContent}";
            }

            var request = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = userMessage }
                },
                // Use low temperature for deterministic extraction
                temperature = 0.1,
                max_tokens = 300
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

            var result = await response.Content.ReadFromJsonAsync<DistillationChatResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cts.Token);

            var messageContent = result?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(messageContent))
            {
                return new DistillationResult(null, "Empty response from OpenRouter");
            }

            var trimmed = messageContent.Trim();

            // Check for NONE response — no durable fact
            if (trimmed.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Distillation returned NONE for content (len={Len})", rawContent.Length);
                return new DistillationResult(null);
            }

            var distilled = trimmed;

            _logger.LogInformation(
                "Distilled content (len={InputLen} -> {OutputLen}): {Preview}",
                rawContent.Length, distilled.Length,
                distilled.Length > 100 ? distilled[..100] + "..." : distilled);

            return new DistillationResult(distilled);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Distillation timed out after 30s");
            return new DistillationResult(null, "Distillation timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Distillation failed");
            return new DistillationResult(null, $"Distillation error: {ex.Message}");
        }
    }
}

#pragma warning disable IDE1006
internal class DistillationChatResponse
{
    public DistillationChatChoice[]? Choices { get; set; }
}

internal class DistillationChatChoice
{
    public DistillationChatMessage? Message { get; set; }
}

internal class DistillationChatMessage
{
    public string? Content { get; set; }
}
#pragma warning restore IDE1006