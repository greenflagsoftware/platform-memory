using System.Text.Json.Serialization;
using AgentMemory.Processing;
using AgentMemory.Storage;

namespace AgentMemory.Capture;

public static class CaptureEndpoints
{
    private const double DefaultSaveThreshold = 0.5;
    private const string SaveThresholdConfigKey = "Memory:SaveThreshold";

    public static void MapCaptureEndpoints(this WebApplication app)
    {
        var threshold = app.Configuration.GetValue<double>(SaveThresholdConfigKey, DefaultSaveThreshold);

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