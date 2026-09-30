using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using AgentMemory.Processing;
using AgentMemory.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentMemory.Tests;

/// <summary>
/// The minimum-content gate must reject bare acknowledgements on a WHOLE-MESSAGE match only.
/// Substring matching previously rejected anything containing "ok" (hooks, token, lookup...).
/// </summary>
public class CaptureGateTests
{
    private readonly Mock<IClassificationService> _classification = new();
    private readonly InMemoryCaptureRepository _captureRepo = new();

    private CaptureProcessor CreateProcessor(Dictionary<string, string?>? extraConfig = null)
    {
        var settings = new Dictionary<string, string?> { ["Memory:SaveThreshold"] = "2.5" };
        if (extraConfig != null)
        {
            foreach (var kv in extraConfig) settings[kv.Key] = kv.Value;
        }

        // Classifier scores everything below threshold: we only care whether it was reached.
        _classification
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClassificationResult("other", 0.0, null));

        return new CaptureProcessor(
            _captureRepo,
            _classification.Object,
            new Mock<IDistillationService>().Object,
            new Mock<IEmbeddingService>().Object,
            new InMemoryMemoryRepository(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new Mock<ILogger<CaptureProcessor>>().Object);
    }

    private async Task<bool> ReachedClassifierAsync(CaptureProcessor processor, string content)
    {
        _captureRepo.Captures[1] = new CaptureRecord
        {
            Id = 1, SessionId = "s", HookEvent = "UserPromptSubmit", RawContent = content
        };
        _classification.Invocations.Clear();
        await processor.ProcessCaptureAsync(1);
        return _classification.Invocations.Count > 0;
    }

    [Theory]
    [InlineData("Yes, commit and push.")]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("  OK!  ")]
    [InlineData("Go ahead")]
    [InlineData("DONE.")]
    [InlineData("Thank you!")]
    public async Task Bare_Acknowledgements_Are_Rejected_Before_Classification(string content)
    {
        var processor = CreateProcessor();

        Assert.False(await ReachedClassifierAsync(processor, content));
    }

    [Theory]
    // "hooks", "token", "lookup", "broken", "abandoned", "yesterday" contain ok/done/yes as substrings.
    [InlineData("Configuration note: the PowerShell hooks read the stdin payload.")]
    [InlineData("The token lookup was broken, so the request was abandoned.")]
    [InlineData("Yesterday we decided to keep pgvector for embeddings.")]
    // Contains a rejection phrase but is a real, longer message.
    [InlineData("Yes please commit and push the hook fixes to origin main once tests pass.")]
    [InlineData("Please continue refactoring the retrieval service to use the new repository.")]
    public async Task Real_Content_Containing_Rejection_Words_Is_Not_Rejected(string content)
    {
        var processor = CreateProcessor();

        Assert.True(await ReachedClassifierAsync(processor, content));
    }

    [Fact]
    public async Task Configured_Patterns_Replace_The_Defaults()
    {
        var processor = CreateProcessor(new()
        {
            ["Memory:Gate:RejectionPatterns:0"] = "roger that",
            ["Memory:Gate:MinWords"] = "1",
        });

        // Custom pattern is a whole-message match (padded to clear the 3-word minimum).
        Assert.False(await ReachedClassifierAsync(processor, "Roger that!"));
        // "yes commit and push" is only in the built-in defaults; a configured list replaces them.
        Assert.True(await ReachedClassifierAsync(processor, "Yes, commit and push."));
    }

    [Fact]
    public async Task Short_Content_With_A_Code_Token_Passes_The_Min_Words_Check()
    {
        var processor = CreateProcessor();

        Assert.True(await ReachedClassifierAsync(processor, "src/AgentMemory/Program.cs"));
        Assert.False(await ReachedClassifierAsync(processor, "just two"));
    }
}
