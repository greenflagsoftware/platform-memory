using System.Text.Json.Serialization;
using AgentMemory.Storage;

namespace AgentMemory.Capture;

public static class CaptureEndpoints
{
    public static void MapCaptureEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/capture");

        group.MapPost("/", async (CaptureRequest request, AppDbContext db, ILogger<CaptureRequest> logger) =>
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

            logger.LogInformation(
                "Captured event {HookEvent} for session {SessionId} (id={CaptureId})",
                record.HookEvent, record.SessionId, record.Id);

            return Results.Accepted($"/capture/{record.Id}", new
            {
                id = record.Id,
                status = "accepted"
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