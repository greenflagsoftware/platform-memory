using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Capture;

namespace AgentMemory.Tests;

public class CaptureRequestTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void Deserialize_SnakeCaseJson_To_CaptureRequest()
    {
        // Arrange
        var json = """
            {
                "session_id": "session-123",
                "hook_event": "UserPromptSubmit",
                "raw_content": "Hello memory!",
                "metadata": "{\"source\": \"test\"}"
            }
            """;

        // Act
        var request = JsonSerializer.Deserialize<CaptureRequest>(json, JsonOptions);

        // Assert
        Assert.NotNull(request);
        Assert.Equal("session-123", request.SessionId);
        Assert.Equal("UserPromptSubmit", request.HookEvent);
        Assert.Equal("Hello memory!", request.RawContent);
        Assert.Equal("{\"source\": \"test\"}", request.Metadata);
    }

    [Fact]
    public void Deserialize_Missing_Metadata_Defaults_To_Null()
    {
        // Arrange
        var json = """
            {
                "session_id": "session-456",
                "hook_event": "PostToolUse",
                "raw_content": "Tool output"
            }
            """;

        // Act
        var request = JsonSerializer.Deserialize<CaptureRequest>(json, JsonOptions);

        // Assert
        Assert.NotNull(request);
        Assert.Equal("session-456", request.SessionId);
        Assert.Equal("PostToolUse", request.HookEvent);
        Assert.Equal("Tool output", request.RawContent);
        Assert.Null(request.Metadata);
    }

    [Fact]
    public void Deserialize_Empty_RawContent_Is_Empty_String()
    {
        // Arrange
        var json = """
            {
                "session_id": "session-789",
                "hook_event": "Stop",
                "raw_content": ""
            }
            """;

        // Act
        var request = JsonSerializer.Deserialize<CaptureRequest>(json, JsonOptions);

        // Assert
        Assert.NotNull(request);
        Assert.Equal("", request.RawContent);
    }

    [Fact]
    public void CaptureRequest_Serializes_With_SnakeCase_PropertyNames()
    {
        // Verify the JSON property names match what the PowerShell hooks send,
        // using SnakeCaseLower naming policy (matching the hooks' JSON payloads).
        var request = new CaptureRequest("s1", "UserPromptSubmit", "content", null);
        var json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.Contains("\"session_id\"", json);
        Assert.Contains("\"hook_event\"", json);
        Assert.Contains("\"raw_content\"", json);
        Assert.Contains("\"metadata\"", json);
    }

    [Fact]
    public void CaptureRequest_PascalCase_Properties_Exist()
    {
        // Ensure the C# type has the expected PascalCase properties for code usage
        var request = new CaptureRequest("s1", "PostToolUse", "content", "{}");

        Assert.Equal("s1", request.SessionId);
        Assert.Equal("PostToolUse", request.HookEvent);
        Assert.Equal("content", request.RawContent);
        Assert.Equal("{}", request.Metadata);
    }
}