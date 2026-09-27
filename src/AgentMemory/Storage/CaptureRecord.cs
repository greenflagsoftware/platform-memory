using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgentMemory.Storage;

public class CaptureRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>
    /// Identifier for the Claude Code session this event originated from.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// The hook event type: 'UserPromptSubmit', 'PostToolUse', or 'Stop'.
    /// </summary>
    public string HookEvent { get; set; } = string.Empty;

    /// <summary>
    /// The raw prompt text, tool-call text, or session-summary text from the event.
    /// </summary>
    public string RawContent { get; set; } = string.Empty;

    /// <summary>
    /// Arbitrary JSON metadata provided by the hook (model, timestamp, session context, etc.).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string Metadata { get; set; } = "{}";

    /// <summary>
    /// When the capture was recorded by the server.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}