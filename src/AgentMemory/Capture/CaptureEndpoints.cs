using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Processing;
using AgentMemory.Storage;

namespace AgentMemory.Capture;

public static class CaptureEndpoints
{
    private const double DefaultSaveThreshold = 0.5;
    private const string SaveThresholdConfigKey = "Memory:SaveThreshold";
    private const string IgnoredToolsConfigKey = "Memory:IgnoredTools";

    /// <summary>
    /// Default list of tool names whose captures are rejected server-side as a backstop.
    /// Mirrors the allowlist in hooks/PostToolUse.ps1.
    /// </summary>
    private static readonly string[] DefaultIgnoredTools =
    [
        "Read", "Glob", "Grep", "ToolSearch", "Fetch", "WebSearch", "WebFetch",
        "Browse", "AskUser", "AskQuestion"
    ];

    public static void MapCaptureEndpoints(this WebApplication app)
    {
        var threshold = app.Configuration.GetValue<double>(SaveThresholdConfigKey, DefaultSaveThreshold);
        var ignoredTools = app.Configuration.GetSection(IgnoredToolsConfigKey)
            .Get<string[]>() ?? DefaultIgnoredTools;

        var group = app.MapGroup("/capture");

        group.MapPost("/", async (CaptureRequest request, AppDbContext db,
            IServiceScopeFactory scopeFactory, ILogger<CaptureRequest> logger) =>
        {
            var record = new CaptureRecord
            {
                SessionId = request.SessionId,
                HookEvent = request.HookEvent,
                RawContent = request.RawContent,
                Metadata = string.IsNullOrWhiteSpace(request.Metadata)
                    ? "{}"
                    : request.Metadata,
            };

            db.Captures.Add(record);
            await db.SaveChangesAsync();

            var captureId = record.Id;

            logger.LogInformation(
                "Captured event {HookEvent} for session {SessionId} (id={CaptureId})",
                record.HookEvent, record.SessionId, captureId);

            // ── Phase 1c: Server-side denylist backstop ──────────────
            // Check the capture's tool_name against IgnoredTools list.
            // If the tool is on the deny list, skip processing entirely.
            string? toolName = null;
            if (!string.IsNullOrWhiteSpace(request.Metadata))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<JsonElement>(request.Metadata);
                    if (meta.TryGetProperty("tool_name", out var tn))
                    {
                        toolName = tn.GetString();
                    }
                }
                catch { /* best-effort parse */ }
            }

            if (toolName != null && Array.Exists(ignoredTools, t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation(
                    "Skipping processing for capture {CaptureId}: tool '{ToolName}' is on IgnoredTools list",
                    captureId, toolName);
                return Results.Accepted($"/capture/{captureId}", new
                {
                    id = captureId,
                    status = "accepted",
                    skipped = "ignored_tool",
                    save_threshold = threshold
                });
            }

            // Fire-and-forget background classification + embedding + storage. This runs
            // after the HTTP response is sent, by which point the request's own DI scope
            // (and its scoped AppDbContext/CaptureProcessor) will have been disposed — so a
            // fresh scope is created here rather than reusing the request's injected services.
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var scopedProcessor = scope.ServiceProvider.GetRequiredService<CaptureProcessor>();
                try
                {
                    await scopedProcessor.ProcessCaptureAsync(captureId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background processing failed for capture {CaptureId}", captureId);
                }
            });

            return Results.Accepted($"/capture/{captureId}", new
            {
                id = captureId,
                status = "accepted",
                save_threshold = threshold
            });
        })
        .WithName("CaptureEvent");
    }
}

/// <summary>
/// Payload received from a Claude Code hook.
/// </summary>
public record CaptureRequest(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("hook_event")] string HookEvent,
    [property: JsonPropertyName("raw_content")] string RawContent,
    [property: JsonPropertyName("metadata")] string? Metadata
);