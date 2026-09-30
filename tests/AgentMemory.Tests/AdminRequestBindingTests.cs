using System.Text.Json;
using AgentMemory.Capture;

namespace AgentMemory.Tests;

/// <summary>
/// Minimal APIs bind request bodies with JsonSerializerDefaults.Web (camelCase, case-insensitive),
/// silently ignoring unknown properties. These tests pin the snake_case names the API documents:
/// if "dry_run" were ignored, a destructive request would quietly run as a dry run (or, for a
/// dry-run request, the reverse if a default ever flipped).
/// </summary>
public class AdminRequestBindingTests
{
    private static T Bind<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void ReprocessRequest_Binds_Snake_Case_Properties()
    {
        var request = Bind<ReprocessRequest>("""{"capture_ids":[1,2,3],"dry_run":false}""");

        Assert.Equal(new long[] { 1, 2, 3 }, request.CaptureIds);
        Assert.False(request.DryRun);
    }

    [Fact]
    public void ReprocessRequest_Defaults_To_Dry_Run_When_Empty()
    {
        var request = Bind<ReprocessRequest>("{}");

        Assert.Null(request.CaptureIds);
        Assert.True(request.DryRun ?? true);
    }

    [Fact]
    public void PruneRequest_Binds_Snake_Case_Properties()
    {
        var request = Bind<PruneRequest>(
            """{"category":"tool_call","max_score":1.5,"ids":[7,8],"dry_run":false}""");

        Assert.Equal("tool_call", request.Category);
        Assert.Equal(1.5, request.MaxScore);
        Assert.Equal(new long[] { 7, 8 }, request.Ids);
        Assert.False(request.DryRun);
    }
}
