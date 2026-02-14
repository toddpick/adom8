using System.Text.Json;
using AIAgents.Core.Models;

namespace AIAgents.Core.Tests.Models;

/// <summary>
/// Tests for JSON serialization/deserialization of all model classes.
/// Verifies that models round-trip correctly through System.Text.Json.
/// </summary>
public sealed class ModelSerializationTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region StoryState

    [Fact]
    public void StoryState_RoundTrips()
    {
        var state = new StoryState
        {
            WorkItemId = 12345,
            CurrentState = "AI Code"
        };
        state.Agents["Planning"] = AgentStatus.Completed();
        state.Artifacts.Code.Add("src/file.cs");
        state.Decisions.Add(new Decision
        {
            Agent = "Planning",
            DecisionText = "Use DI",
            Rationale = "Testability"
        });
        state.Questions.Add(new Question
        {
            Agent = "Review",
            QuestionText = "Is this approach correct?"
        });
        state.TokenUsage.RecordUsage("Planning", new TokenUsageData
        {
            InputTokens = 100, OutputTokens = 50, TotalTokens = 150,
            EstimatedCost = 0.001m, Model = "gpt-4o"
        });

        var json = JsonSerializer.Serialize(state, s_options);
        var deserialized = JsonSerializer.Deserialize<StoryState>(json, s_options);

        Assert.NotNull(deserialized);
        Assert.Equal(12345, deserialized!.WorkItemId);
        Assert.Equal("AI Code", deserialized.CurrentState);
        Assert.True(deserialized.Agents.ContainsKey("Planning"));
        Assert.Contains("src/file.cs", deserialized.Artifacts.Code);
        Assert.Single(deserialized.Decisions);
        Assert.Single(deserialized.Questions);
        Assert.Equal(150, deserialized.TokenUsage.TotalTokens);
    }

    [Fact]
    public void StoryState_DefaultValues_Correct()
    {
        var state = new StoryState { WorkItemId = 1, CurrentState = "New" };

        Assert.Empty(state.Agents);
        Assert.Empty(state.Artifacts.Code);
        Assert.Empty(state.Artifacts.Tests);
        Assert.Empty(state.Artifacts.Docs);
        Assert.Empty(state.Decisions);
        Assert.Empty(state.Questions);
        Assert.Equal(0, state.TokenUsage.TotalTokens);
    }

    #endregion

    #region AgentStatus

    [Fact]
    public void AgentStatus_Pending_HasCorrectStatus()
    {
        var status = AgentStatus.Pending();
        Assert.Equal("pending", status.Status);
        Assert.Null(status.StartedAt);
        Assert.Null(status.CompletedAt);
    }

    [Fact]
    public void AgentStatus_InProgress_SetsStartedAt()
    {
        var before = DateTime.UtcNow;
        var status = AgentStatus.InProgress();

        Assert.Equal("in_progress", status.Status);
        Assert.NotNull(status.StartedAt);
        Assert.True(status.StartedAt >= before);
    }

    [Fact]
    public void AgentStatus_Completed_SetsCompletedAt()
    {
        var before = DateTime.UtcNow;
        var status = AgentStatus.Completed();

        Assert.Equal("completed", status.Status);
        Assert.NotNull(status.CompletedAt);
        Assert.True(status.CompletedAt >= before);
    }

    [Fact]
    public void AgentStatus_Failed_IncludesReason()
    {
        var status = AgentStatus.Failed("Network timeout");

        Assert.Equal("failed", status.Status);
        Assert.NotNull(status.AdditionalData);
        Assert.Equal("Network timeout", status.AdditionalData!["error"]);
    }

    [Fact]
    public void AgentStatus_Failed_NullReason_NoAdditionalData()
    {
        var status = AgentStatus.Failed();

        Assert.Equal("failed", status.Status);
        Assert.Null(status.AdditionalData);
    }

    [Fact]
    public void AgentStatus_RoundTrips()
    {
        var status = AgentStatus.Completed();
        status.AdditionalData = new Dictionary<string, object> { ["score"] = 85 };

        var json = JsonSerializer.Serialize(status, s_options);
        var deserialized = JsonSerializer.Deserialize<AgentStatus>(json, s_options);

        Assert.NotNull(deserialized);
        Assert.Equal("completed", deserialized!.Status);
    }

    #endregion

    #region StoryWorkItem

    [Fact]
    public void StoryWorkItem_DefaultAutonomyLevel_Is3()
    {
        var wi = new StoryWorkItem
        {
            Id = 1,
            Title = "Test",
            State = "New"
        };

        Assert.Equal(3, wi.AutonomyLevel);
        Assert.Equal(85, wi.MinimumReviewScore);
    }

    [Fact]
    public void StoryWorkItem_AllProperties_SetCorrectly()
    {
        var wi = new StoryWorkItem
        {
            Id = 100,
            Title = "Test Story",
            Description = "Description",
            AcceptanceCriteria = "AC",
            State = "Active",
            AssignedTo = "TestUser",
            AreaPath = "Project\\Area",
            IterationPath = "Project\\Sprint1",
            StoryPoints = 8,
            Tags = ["tag1", "tag2"],
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ChangedDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            AutonomyLevel = 5,
            MinimumReviewScore = 90
        };

        Assert.Equal(100, wi.Id);
        Assert.Equal("Test Story", wi.Title);
        Assert.Equal(5, wi.AutonomyLevel);
        Assert.Equal(90, wi.MinimumReviewScore);
        Assert.Equal(2, wi.Tags.Count);
    }

    #endregion

    #region PlanningResult

    [Fact]
    public void PlanningResult_AllProperties_Required()
    {
        var result = new PlanningResult
        {
            ProblemAnalysis = "Analysis",
            TechnicalApproach = "Approach",
            AffectedFiles = ["file1.cs"],
            Complexity = 8,
            Architecture = "Clean",
            SubTasks = ["Task 1"],
            Dependencies = ["Dep 1"],
            Risks = ["Risk 1"],
            Assumptions = ["Assume 1"],
            TestingStrategy = "Unit tests"
        };

        Assert.Equal("Analysis", result.ProblemAnalysis);
        Assert.Single(result.AffectedFiles);
        Assert.Equal(8, result.Complexity);
    }

    #endregion

    #region CodeReviewResult

    [Fact]
    public void CodeReviewResult_WithIssues_TracksCounts()
    {
        var result = new CodeReviewResult
        {
            Score = 75,
            Recommendation = "Approve with Comments",
            Summary = "Good overall",
            CriticalIssues = [new ReviewIssue { Line = 10, Issue = "SQL injection", Fix = "Parameterize", Code = "bad code" }],
            HighIssues = [new ReviewIssue { Issue = "Missing null check" }],
            MediumIssues = [],
            LowIssues = [new ReviewIssue { Issue = "Naming convention" }],
            PositiveFindings = ["Good structure"]
        };

        Assert.Equal(75, result.Score);
        Assert.Single(result.CriticalIssues);
        Assert.Equal(10, result.CriticalIssues[0].Line);
        Assert.Single(result.HighIssues);
        Assert.Empty(result.MediumIssues);
    }

    #endregion

    #region CodeFile and TestCase

    [Fact]
    public void CodeFile_DefaultIsNew_IsTrue()
    {
        var file = new CodeFile
        {
            RelativePath = "src/Test.cs",
            Content = "content"
        };

        Assert.True(file.IsNew);
    }

    [Fact]
    public void TestCase_AllProperties_SetCorrectly()
    {
        var tc = new TestCase
        {
            Name = "Test_Success",
            Type = "Unit",
            Priority = "High",
            Description = "Test desc",
            ExpectedResult = "Returns true"
        };

        Assert.Equal("Test_Success", tc.Name);
        Assert.Equal("Unit", tc.Type);
    }

    #endregion

    #region Decision and Question

    [Fact]
    public void Decision_DefaultTimestamp_IsUtcNow()
    {
        var before = DateTime.UtcNow;
        var decision = new Decision
        {
            Agent = "Planning",
            DecisionText = "Use X",
            Rationale = "Because Y"
        };

        Assert.True(decision.Timestamp >= before);
    }

    [Fact]
    public void Question_DefaultAskedTo_IsTeam()
    {
        var question = new Question
        {
            Agent = "Review",
            QuestionText = "Should we?"
        };

        Assert.Equal("team", question.AskedTo);
        Assert.Null(question.Answer);
    }

    #endregion

    #region ArtifactPaths

    [Fact]
    public void ArtifactPaths_DefaultLists_AreEmpty()
    {
        var paths = new ArtifactPaths();

        Assert.Empty(paths.Code);
        Assert.Empty(paths.Tests);
        Assert.Empty(paths.Docs);
    }

    [Fact]
    public void ArtifactPaths_RoundTrips()
    {
        var paths = new ArtifactPaths();
        paths.Code.Add("src/A.cs");
        paths.Tests.Add("tests/B.cs");
        paths.Docs.Add("docs/C.md");

        var json = JsonSerializer.Serialize(paths, s_options);
        var deserialized = JsonSerializer.Deserialize<ArtifactPaths>(json, s_options);

        Assert.NotNull(deserialized);
        Assert.Contains("src/A.cs", deserialized!.Code);
        Assert.Contains("tests/B.cs", deserialized.Tests);
        Assert.Contains("docs/C.md", deserialized.Docs);
    }

    #endregion

    #region AICompletionResult

    [Fact]
    public void AICompletionResult_ImplicitConversionToString()
    {
        var result = new AICompletionResult
        {
            Content = "Hello world",
            Usage = null
        };

        string text = result;
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void AICompletionResult_WithUsage_Properties()
    {
        var result = new AICompletionResult
        {
            Content = "Generated code",
            Usage = new TokenUsageData
            {
                InputTokens = 100,
                OutputTokens = 200,
                TotalTokens = 300,
                EstimatedCost = 0.01m,
                Model = "gpt-4o"
            }
        };

        Assert.Equal("Generated code", result.Content);
        Assert.NotNull(result.Usage);
        Assert.Equal(300, result.Usage!.TotalTokens);
    }

    #endregion

    #region CodebaseAnalysis Models

    [Fact]
    public void AnalyzeCodebaseRequest_Defaults()
    {
        var request = new AnalyzeCodebaseRequest();

        Assert.Equal("6months", request.UserStoryTimeframe);
        Assert.Equal("standard", request.AnalysisDepth);
        Assert.True(request.IncludeGitHistory);
        Assert.False(request.Incremental);
    }

    [Fact]
    public void CodebaseAnalysisMetadata_RoundTrips()
    {
        var metadata = new CodebaseAnalysisMetadata
        {
            LastAnalysis = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            FilesAnalyzed = 500,
            LinesOfCode = 50000,
            LanguagesDetected = ["C#", "TypeScript"],
            PrimaryFramework = ".NET 8"
        };

        var json = JsonSerializer.Serialize(metadata);
        var deserialized = JsonSerializer.Deserialize<CodebaseAnalysisMetadata>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(500, deserialized!.FilesAnalyzed);
        Assert.Equal(50000, deserialized.LinesOfCode);
        Assert.Contains("C#", deserialized.LanguagesDetected);
    }

    #endregion
}
