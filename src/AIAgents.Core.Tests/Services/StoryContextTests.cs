using AIAgents.Core.Models;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIAgents.Core.Tests.Services;

/// <summary>
/// Tests for StoryContext covering state persistence, artifact I/O, and edge cases.
/// Uses real temp directories that are cleaned up after each test.
/// </summary>
public sealed class StoryContextTests : IDisposable
{
    private readonly string _tempDir;

    public StoryContextTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ado-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private StoryContext CreateContext(int workItemId = 12345)
    {
        return new StoryContext(workItemId, _tempDir, NullLogger<StoryContext>.Instance);
    }

    [Fact]
    public void Constructor_CreatesStoryDirectory()
    {
        var ctx = CreateContext(100);

        Assert.True(Directory.Exists(ctx.StoryDirectory));
        Assert.Contains("US-100", ctx.StoryDirectory);
    }

    [Fact]
    public void WorkItemId_ReturnsCorrectValue()
    {
        var ctx = CreateContext(42);
        Assert.Equal(42, ctx.WorkItemId);
    }

    [Fact]
    public async Task LoadStateAsync_NoExistingFile_CreatesDefaultState()
    {
        var ctx = CreateContext();

        var state = await ctx.LoadStateAsync();

        Assert.NotNull(state);
        Assert.Equal(12345, state.WorkItemId);
        Assert.Equal("New", state.CurrentState);
    }

    [Fact]
    public async Task SaveStateAsync_ThenLoad_RoundTrips()
    {
        var ctx = CreateContext(200);

        var state = new StoryState
        {
            WorkItemId = 200,
            CurrentState = "AI Code"
        };
        state.Agents["Planning"] = AgentStatus.Completed();
        state.Artifacts.Code.Add("src/MyFile.cs");

        await ctx.SaveStateAsync(state);

        var loaded = await ctx.LoadStateAsync();

        Assert.Equal(200, loaded.WorkItemId);
        Assert.Equal("AI Code", loaded.CurrentState);
        Assert.True(loaded.Agents.ContainsKey("Planning"));
        Assert.Equal("completed", loaded.Agents["Planning"].Status);
        Assert.Contains("src/MyFile.cs", loaded.Artifacts.Code);
    }

    [Fact]
    public async Task SaveStateAsync_UpdatesTimestamp()
    {
        var ctx = CreateContext();
        var state = new StoryState { WorkItemId = 12345, CurrentState = "New" };
        var before = DateTime.UtcNow;

        await ctx.SaveStateAsync(state);

        Assert.True(state.UpdatedAt >= before);
    }

    [Fact]
    public async Task WriteArtifactAsync_ThenRead_ReturnsContent()
    {
        var ctx = CreateContext();

        await ctx.WriteArtifactAsync("PLAN.md", "# My Plan\nThis is the plan.");

        var content = await ctx.ReadArtifactAsync("PLAN.md");

        Assert.Equal("# My Plan\nThis is the plan.", content);
    }

    [Fact]
    public async Task WriteArtifactAsync_NestedPath_CreatesDirectories()
    {
        var ctx = CreateContext();

        await ctx.WriteArtifactAsync("subdirectory/deep/file.txt", "Nested content");

        var content = await ctx.ReadArtifactAsync("subdirectory/deep/file.txt");
        Assert.Equal("Nested content", content);
    }

    [Fact]
    public async Task ReadArtifactAsync_NonExistent_ReturnsNull()
    {
        var ctx = CreateContext();

        var content = await ctx.ReadArtifactAsync("does-not-exist.md");

        Assert.Null(content);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var ctx = CreateContext();
        await ctx.DisposeAsync(); // Should not throw
    }

    [Fact]
    public async Task LoadStateAsync_CorruptedJson_ResetsToDefault()
    {
        var ctx = CreateContext(300);
        var stateFile = Path.Combine(ctx.StoryDirectory, "state.json");

        // Write invalid JSON to state file
        await File.WriteAllTextAsync(stateFile, "{ invalid json <<<");

        // Load should detect the error and create a default state
        // (the deserialization returns null, which triggers the reset)
        var state = await ctx.LoadStateAsync();

        Assert.NotNull(state);
        Assert.Equal(300, state.WorkItemId);
    }

    [Fact]
    public async Task SaveStateAsync_PreservesTokenUsage()
    {
        var ctx = CreateContext(400);

        var state = new StoryState { WorkItemId = 400, CurrentState = "AI Code" };
        state.TokenUsage.RecordUsage("Planning", new TokenUsageData
        {
            InputTokens = 1000,
            OutputTokens = 500,
            TotalTokens = 1500,
            EstimatedCost = 0.01m,
            Model = "gpt-4o"
        });

        await ctx.SaveStateAsync(state);
        var loaded = await ctx.LoadStateAsync();

        Assert.Equal(1500, loaded.TokenUsage.TotalTokens);
        Assert.Equal(0.01m, loaded.TokenUsage.TotalCost);
        Assert.True(loaded.TokenUsage.Agents.ContainsKey("Planning"));
    }

    [Fact]
    public async Task SaveStateAsync_PreservesDecisionsAndQuestions()
    {
        var ctx = CreateContext(500);

        var state = new StoryState { WorkItemId = 500, CurrentState = "AI Review" };
        state.Decisions.Add(new Decision
        {
            Agent = "Planning",
            DecisionText = "Use clean architecture",
            Rationale = "Better separation of concerns"
        });
        state.Questions.Add(new Question
        {
            Agent = "Review",
            QuestionText = "Should we use async streams?"
        });

        await ctx.SaveStateAsync(state);
        var loaded = await ctx.LoadStateAsync();

        Assert.Single(loaded.Decisions);
        Assert.Equal("Use clean architecture", loaded.Decisions[0].DecisionText);
        Assert.Single(loaded.Questions);
        Assert.Equal("Should we use async streams?", loaded.Questions[0].QuestionText);
    }

    [Fact]
    public async Task MultipleContexts_SameWorkItem_ShareDirectory()
    {
        var ctx1 = CreateContext(600);
        await ctx1.WriteArtifactAsync("shared.txt", "Written by ctx1");

        var ctx2 = CreateContext(600);
        var content = await ctx2.ReadArtifactAsync("shared.txt");

        Assert.Equal("Written by ctx1", content);
    }
}
