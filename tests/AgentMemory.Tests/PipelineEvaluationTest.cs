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
/// Phase 0 evaluation test: runs the current pipeline (gate → classify → threshold → distill → embed)
/// against the labeled fixture set and reports precision/recall for the "keep" class.
///
/// Fixtures are embedded in FixtureData.cs and loaded at test construction time.
/// The classifier and distiller mocks use per-content setup to return deterministic results.
///
/// Run with: dotnet test --filter "FullyQualifiedName~PipelineEvaluation"
/// </summary>
public class PipelineEvaluationTest
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Memory:SaveThreshold"] = "2.5",
            ["Memory:Gate:MinWords"] = "3",
            ["Memory:Gate:RejectionPatterns"] = "yes, commit and push,yes,ok,go ahead,commit and push,done,proceed,continue",
            ["Memory:DedupSimilarity"] = "1.1"
        })!
        .Build();

    private static readonly List<FixtureEntry> Fixtures = FixtureData.Load();

    [Fact]
    public async Task Pipeline_Evaluation_Reports_Precision_And_Recall()
    {
        var loggerMock = new Mock<ILogger<CaptureProcessor>>();
        var classificationMock = new Mock<IClassificationService>();
        var distillationMock = new Mock<IDistillationService>();
        var embeddingMock = new Mock<IEmbeddingService>();
        var captureRepo = new InMemoryCaptureRepository();
        var memoryRepo = new InMemoryMemoryRepository();

        foreach (var f in Fixtures)
        {
            captureRepo.Captures[f.Id] = new CaptureRecord
            {
                Id = f.Id,
                SessionId = "eval-session",
                HookEvent = f.HookEvent,
                RawContent = f.RawContent
            };
        }

        var processor = new CaptureProcessor(
            captureRepo,
            classificationMock.Object,
            distillationMock.Object,
            embeddingMock.Object,
            memoryRepo,
            Config,
            loggerMock.Object);

        // ── Setup mocks ─────────────────────────────────────────
        // Use a single catch-all Returns for each mock with a lookup,
        // to avoid CancellationToken lambda issues with per-content setups.
        classificationMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((content, _) =>
            {
                var fixture = Fixtures.FirstOrDefault(f => f.RawContent == content);
                if (fixture != null)
                    return Task.FromResult(new ClassificationResult(
                        fixture.Label == "keep" ? "decision" : "tool_call",
                        fixture.ExpectedScore,
                        null));
                return Task.FromResult(new ClassificationResult("unknown", 0.0, "fixture not found"));
            });

        distillationMock
            .Setup(d => d.DistillAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((content, ctx, _) =>
            {
                var fixture = Fixtures.FirstOrDefault(f => f.RawContent == content);
                if (fixture != null && fixture.ExpectedScore >= 2.5)
                    return Task.FromResult(new DistillationResult($"Distilled: {fixture.Reason}"));
                return Task.FromResult(new DistillationResult(null));
            });

        embeddingMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) =>
                Task.FromResult<float[]?>(new float[] { 0.1f, 0.2f, 0.3f }));

        // ── Run pipeline for each fixture ─────────────────────────
        var results = new List<EvaluationResult>();
        foreach (var fixture in Fixtures)
        {
            await processor.ProcessCaptureAsync(fixture.Id);
            var wasStored = memoryRepo.Memories.Values.Any(m => m.CaptureId == fixture.Id);
            results.Add(new EvaluationResult(fixture.Id, fixture.Label, wasStored ? "keep" : "drop"));
        }

        // ── Compute metrics ───────────────────────────────────────
        var truePositives = results.Count(r => r.Actual == "keep" && r.Expected == "keep");
        var falsePositives = results.Count(r => r.Actual == "keep" && r.Expected == "drop");
        var falseNegatives = results.Count(r => r.Actual == "drop" && r.Expected == "keep");
        var trueNegatives = results.Count(r => r.Actual == "drop" && r.Expected == "drop");

        var precision = truePositives > 0
            ? truePositives / (double)(truePositives + falsePositives)
            : 0.0;
        var recall = truePositives > 0
            ? truePositives / (double)(truePositives + falseNegatives)
            : 0.0;
        var accuracy = (truePositives + trueNegatives) / (double)results.Count;
        var f1 = (precision + recall) > 0
            ? 2 * precision * recall / (precision + recall)
            : 0.0;

        // ── Report ────────────────────────────────────────────────
        var reportLines = new List<string>
        {
            "",
            "═══════════════════════════════════════════════════",
            "  Phase 0 Pipeline Evaluation Results",
            "═══════════════════════════════════════════════════",
            $"  Total fixtures : {Fixtures.Count}",
            $"  True positives  : {truePositives}  (correctly kept)",
            $"  False positives : {falsePositives}  (kept when should drop)",
            $"  False negatives : {falseNegatives}  (dropped when should keep)",
            $"  True negatives  : {trueNegatives}  (correctly dropped)",
            $"",
            $"  Precision      : {precision:P2}",
            $"  Recall         : {recall:P2}",
            $"  F1 Score       : {f1:P2}",
            $"  Accuracy       : {accuracy:P2}",
            "═══════════════════════════════════════════════════",
            "",
            "Per-fixture results:",
        };

        foreach (var r in results)
        {
            var fixture = Fixtures.First(f => f.Id == r.FixtureId);
            var icon = r.Actual == r.Expected ? "OK" : "XX";
            reportLines.Add($"  {icon} #{fixture.Id,2} expect={fixture.Label,-5} actual={r.Actual,-5}  score={fixture.ExpectedScore:F1}  {fixture.Reason}");
        }

        var report = string.Join("\n", reportLines);
        Console.WriteLine(report);

        var outputPath = Path.Combine(Path.GetTempPath(), "pipeline-evaluation-report.txt");
        await File.WriteAllTextAsync(outputPath, report);
        Console.WriteLine($"\nFull report written to: {outputPath}");

        var noiseRate = (falsePositives + falseNegatives) / (double)results.Count;
        Assert.True(noiseRate <= 0.50,
            $"Noise rate {noiseRate:P2} exceeds 50% baseline");
    }
}

// ── Fixture entries (from embedded data) ────────────────────────

internal static class FixtureData
{
    public static List<FixtureEntry> Load() => new()
    {
        // ── KEEP (9 fixtures) ─────────────────────────────────────
        new( 1, "PostToolUse",
            "Bash: git commit -m 'Add pgvector HNSW index to memories table' -> success",
            "keep", "State-changing commit with specific detail about the schema change"),
        new( 2, "UserPromptSubmit",
            "We decided to use pgvector with HNSW indexes because cosine distance queries on 1536-d vectors need sub-100ms latency.",
            "keep", "Architectural decision with rationale and specific parameter values"),
        new( 3, "PostToolUse",
            "Write: File written to src/AgentMemory/appsettings.json with Memory:SaveThreshold set to 2.5",
            "keep", "Configuration change"),
        new( 4, "PostToolUse",
            "Bash: docker compose up -d -> (started containers: agent-memory, pgvector)",
            "keep", "Docker compose start — useful how-to"),
        new( 5, "UserPromptSubmit",
            "Use the CompareExchange pattern for lock-free reads on the embedding cache; the standard lock is a bottleneck under concurrent search.",
            "keep", "Non-obvious gotcha about concurrency pattern"),
        new(19, "UserPromptSubmit",
            "What was the decision about the embedding dimension?",
            "keep", "Question about a past decision"),
        new(20, "PostToolUse",
            "Bash: dotnet add package Pgvector.EntityFrameworkCore -> added package v0.3.0",
            "keep", "Package install with specific version"),
        new(21, "PostToolUse",
            "TodoWrite: [{\"description\": \"Fix dedup threshold\", \"status\": \"pending\"}]",
            "keep", "Task tracking — reveals dedup threshold needs tuning"),
        new(22, "PostToolUse",
            "StrReplace: @ src/AgentMemory/appsettings.json: s/0.5/2.5/ -> 1 replacement",
            "keep", "Specific file edit showing config value changed"),

        // ── DROP (13 fixtures) ───────────────────────────────────
        new( 6, "PostToolUse",
            "Bash: ls -la -> (file listing)",
            "drop", "Read-only command output"),
        new( 7, "PostToolUse",
            "Read: Program.cs -> (file contents)",
            "drop", "File read"),
        new( 8, "PostToolUse",
            "Glob: **/*.cs -> 23 files found",
            "drop", "File search"),
        new( 9, "PostToolUse",
            "Grep: MemorySaveThreshold -> src/AgentMemory/appsettings.json: \"SaveThreshold\": 2.5",
            "drop", "Search that found a config value"),
        new(10, "PostToolUse",
            "WebSearch: pgvector HNSW parameters -> (search results)",
            "drop", "Web search"),
        new(11, "PostToolUse",
            "Bash: git log --oneline -5 -> abc1234 Fix typo",
            "drop", "Git log read"),
        new(12, "PostToolUse",
            "Fetch: https://api.github.com/repos/.../issues -> (API response)",
            "drop", "API fetch"),
        new(13, "UserPromptSubmit",
            "Yes, commit and push.",
            "drop", "Bare acknowledgement"),
        new(14, "UserPromptSubmit",
            "ok",
            "drop", "Single-word acknowledgement"),
        new(15, "UserPromptSubmit",
            "go ahead",
            "drop", "Short confirmation"),
        new(16, "UserPromptSubmit",
            "done",
            "drop", "Session-ended acknowledgement"),
        new(17, "Stop",
            "Session ended",
            "drop", "Session summary stub"),
        new(18, "PostToolUse",
            "mcp__agent-memory__search_memories: {\"query\": \"pgvector HNSW\"} -> (memory results)",
            "drop", "AgentMemory's own MCP tool call — feedback loop"),
    };
}

internal class FixtureEntry
{
    public int Id { get; }
    public string HookEvent { get; }
    public string RawContent { get; }
    public string Label { get; }
    public string Reason { get; }

    public FixtureEntry(int id, string hookEvent, string rawContent, string label, string reason)
    {
        Id = id;
        HookEvent = hookEvent;
        RawContent = rawContent;
        Label = label;
        Reason = reason;
    }

    public double ExpectedScore => Label == "keep" ? 3.0 : 0.5;
}

internal record EvaluationResult(
    int FixtureId,
    string Expected,
    string Actual
);