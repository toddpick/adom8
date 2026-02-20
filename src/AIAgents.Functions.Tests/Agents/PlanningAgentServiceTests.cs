using System.Text.Json;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using AIAgents.Functions.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIAgents.Functions.Tests.Agents;

/// <summary>
/// Tests for PlanningAgentService covering happy path, error handling,
/// state transitions, and token tracking.
/// </summary>
public sealed class PlanningAgentServiceTests
{
    private readonly Mock<IAIClientFactory> _aiFactoryMock;
    private readonly Mock<IAIClient> _aiClientMock;
    private readonly Mock<IAzureDevOpsClient> _adoMock;
    private readonly Mock<IGitOperations> _gitMock;
    private readonly Mock<IStoryContextFactory> _contextFactoryMock;
    private readonly Mock<IStoryContext> _contextMock;
    private readonly Mock<ITemplateEngine> _templateMock;
    private readonly Mock<ICodebaseContextProvider> _codebaseMock;
    private readonly Mock<IAgentTaskQueue> _taskQueueMock;

    private StoryState _capturedState = null!;

    public PlanningAgentServiceTests()
    {
        _aiFactoryMock = new Mock<IAIClientFactory>();
        _aiClientMock = new Mock<IAIClient>();
        _adoMock = new Mock<IAzureDevOpsClient>();
        _gitMock = new Mock<IGitOperations>();
        _contextFactoryMock = new Mock<IStoryContextFactory>();
        _contextMock = new Mock<IStoryContext>();
        _templateMock = new Mock<ITemplateEngine>();
        _codebaseMock = new Mock<ICodebaseContextProvider>();
        _taskQueueMock = new Mock<IAgentTaskQueue>();

        _aiFactoryMock.Setup(f => f.GetClientForAgent("Planning", It.IsAny<StoryModelOverrides?>())).Returns(_aiClientMock.Object);
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private PlanningAgentService CreateService()
    {
        return new PlanningAgentService(
            _aiFactoryMock.Object,
            _adoMock.Object,
            _gitMock.Object,
            _contextFactoryMock.Object,
            _templateMock.Object,
            _codebaseMock.Object,
            NullLogger<PlanningAgentService>.Instance,
            _taskQueueMock.Object);
    }

    private void SetupHappyPath(StoryWorkItem? workItem = null, string? aiResponse = null)
    {
        var wi = workItem ?? MockAIResponses.SampleWorkItem();
        var state = MockAIResponses.SampleState(wi.Id);

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(wi);
        _adoMock.Setup(a => a.DownloadSupportingArtifactsAsync(wi.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemSupportingArtifacts());

        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"C:\repos\test");
        _gitMock.Setup(g => g.ListFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "src/Program.cs", "README.md" });
        _gitMock.Setup(g => g.ReadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<button id=\"provision-btn\"></button>\n<button id=\"function-key-btn\"></button>\n<span id=\"codebase-badge\"></span>");

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s)
            .Returns(Task.CompletedTask);

        var response = aiResponse ?? MockAIResponses.ValidPlanningResponse;
        _aiClientMock.Setup(a => a.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult
            {
                Content = response,
                Usage = new TokenUsageData
                {
                    InputTokens = 500, OutputTokens = 1000, TotalTokens = 1500,
                    EstimatedCost = 0.01m, Model = "gpt-4o"
                }
            });

        _templateMock.Setup(t => t.RenderAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Rendered Template");

        _codebaseMock.Setup(c => c.LoadRelevantContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("## Codebase Context\nExisting architecture...");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_CompletesSuccessfully()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        // Verify AI was called
        _aiClientMock.Verify(a => a.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify state was saved
        _contextMock.Verify(c => c.SaveStateAsync(
            It.IsAny<StoryState>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WritesArtifacts()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        _contextMock.Verify(c => c.WriteArtifactAsync("PLAN.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _contextMock.Verify(c => c.WriteArtifactAsync("TASKS.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_CommitsToGit()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        _gitMock.Verify(g => g.CommitAndPushAsync(
            It.IsAny<string>(), It.Is<string>(m => m.Contains("AI Planning")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_UpdatesAdoState()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        _adoMock.Verify(a => a.UpdateWorkItemStateAsync(12345, "AI Agent", It.IsAny<CancellationToken>()), Times.Once);
        _adoMock.Verify(a => a.AddWorkItemCommentAsync(12345, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TracksTokenUsage()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.NotNull(_capturedState);
        Assert.True(_capturedState.TokenUsage.TotalTokens > 0);
        Assert.True(_capturedState.TokenUsage.Agents.ContainsKey("Planning"));
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TransitionsState()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Equal("AI Code", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Planning"].Status);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedAiResponse_UsesFallbackParsing()
    {
        SetupHappyPath(aiResponse: MockAIResponses.MalformedPlanningResponse);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        // Should NOT throw — uses fallback parsing
        await service.ExecuteAsync(task);

        // Still completes
        Assert.Equal("AI Code", _capturedState.CurrentState);
    }

    [Fact]
    public async Task ExecuteAsync_CodeFencedResponse_ParsesCorrectly()
    {
        SetupHappyPath(aiResponse: MockAIResponses.PlanningResponseInCodeFences);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Equal("AI Code", _capturedState.CurrentState);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDescription_NoThrow()
    {
        SetupHappyPath(workItem: MockAIResponses.EmptyWorkItem());
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 99999, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.NotNull(_capturedState);
    }

    [Fact]
    public async Task ExecuteAsync_AddsDecision()
    {
        SetupHappyPath();
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Single(_capturedState.Decisions);
        Assert.Equal("Planning", _capturedState.Decisions[0].Agent);
    }

    [Fact]
    public async Task ExecuteAsync_AiClientThrows_PropagatesException()
    {
        SetupHappyPath();
        _aiClientMock.Setup(a => a.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        var result = await service.ExecuteAsync(task);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_GitFailure_ReturnsFailedResult()
    {
        SetupHappyPath();
        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Git clone failed"));

        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        var result = await service.ExecuteAsync(task);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_AdoUnavailable_ReturnsFailedResult()
    {
        SetupHappyPath();
        _adoMock.Setup(a => a.GetWorkItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("ADO timed out"));

        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        var result = await service.ExecuteAsync(task);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedStory_MovesToNeedsRevision()
    {
        SetupHappyPath(aiResponse: MockAIResponses.RejectedPlanningResponse);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Equal("Needs Revision", _capturedState.CurrentState);
        _adoMock.Verify(a => a.UpdateWorkItemStateAsync(12345, "Needs Revision", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedStory_DoesNotEnqueueCoding()
    {
        SetupHappyPath(aiResponse: MockAIResponses.RejectedPlanningResponse);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        _taskQueueMock.Verify(q => q.EnqueueAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedStory_PostsRejectComment()
    {
        SetupHappyPath(aiResponse: MockAIResponses.RejectedPlanningResponse);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        _adoMock.Verify(a => a.AddWorkItemCommentAsync(12345,
            It.Is<string>(c => c.Contains("Story Not Ready for Coding") && c.Contains("Blockers")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedStory_RecordsTriageDecision()
    {
        SetupHappyPath(aiResponse: MockAIResponses.RejectedPlanningResponse);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Contains(_capturedState.Decisions, d =>
            d.Agent == "Planning" && d.DecisionText.Contains("rejected by triage gate"));
    }

    [Fact]
    public async Task ExecuteAsync_ReadyStory_ProceedsToCoding()
    {
        SetupHappyPath(); // ValidPlanningResponse has proceed=true
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Equal("AI Code", _capturedState.CurrentState);
        _taskQueueMock.Verify(q => q.EnqueueAsync(
            It.Is<AgentTask>(t => t.AgentType == AgentType.Coding),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoReadinessField_ProceedsToCoding()
    {
        // Backward compatibility: if AI response has no readiness field, proceed as before
        SetupHappyPath(aiResponse: MockAIResponses.PlanningResponseNoReadiness);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Equal("AI Code", _capturedState.CurrentState);
        _taskQueueMock.Verify(q => q.EnqueueAsync(
            It.Is<AgentTask>(t => t.AgentType == AgentType.Coding),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ParsePlanningResult_ParsesReadiness()
    {
        var result = PlanningAgentService.ParsePlanningResult(MockAIResponses.ValidPlanningResponse);

        Assert.NotNull(result.Readiness);
        Assert.True(result.Readiness.Proceed);
        Assert.Equal(92, result.Readiness.ReadinessScore);
    }

    [Fact]
    public void ParsePlanningResult_RejectedReadiness()
    {
        var result = PlanningAgentService.ParsePlanningResult(MockAIResponses.RejectedPlanningResponse);

        Assert.NotNull(result.Readiness);
        Assert.False(result.Readiness.Proceed);
        Assert.Equal(35, result.Readiness.ReadinessScore);
        Assert.Equal(2, result.Readiness.Blockers.Count);
        Assert.Equal(2, result.Readiness.Questions.Count);
        Assert.Equal(3, result.Readiness.SuggestedBreakdown.Count);
    }

    [Fact]
    public void ParsePlanningResult_NoReadinessField_ReturnsNull()
    {
        var result = PlanningAgentService.ParsePlanningResult(MockAIResponses.PlanningResponseNoReadiness);

        Assert.Null(result.Readiness);
    }

    [Fact]
    public void ParsePlanningResult_WithResearchNeeded_ParsesCorrectly()
    {
        var result = PlanningAgentService.ParsePlanningResult(MockAIResponses.PlanningResponseWithResearchNeeded);

        Assert.NotNull(result.Readiness);
        Assert.False(result.Readiness.Proceed);
        Assert.Equal(65, result.Readiness.ReadinessScore);
        Assert.Equal(2, result.Readiness.ResearchNeeded.Count);
        Assert.Contains("GitHub Issues API", result.Readiness.ResearchNeeded[0]);
        Assert.Contains("Azure DevOps API", result.Readiness.ResearchNeeded[1]);
    }

    [Fact]
    public async Task ExecuteAsync_WithResearchNeeded_IncludesInComment()
    {
        SetupHappyPath(aiResponse: MockAIResponses.PlanningResponseWithResearchNeeded);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        _adoMock.Verify(a => a.AddWorkItemCommentAsync(12345,
            It.Is<string>(c => c.Contains("Research Needed") && 
                              c.Contains("Unverified External API Assumptions") &&
                              c.Contains("🔍")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithResearchNeeded_MovesToNeedsRevision()
    {
        SetupHappyPath(aiResponse: MockAIResponses.PlanningResponseWithResearchNeeded);
        var service = CreateService();
        var task = new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning };

        await service.ExecuteAsync(task);

        Assert.Equal("Needs Revision", _capturedState.CurrentState);
        _adoMock.Verify(a => a.UpdateWorkItemStateAsync(12345, "Needs Revision", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== PLACEHOLDER DETECTION TESTS ==========

    [Theory]
    [InlineData("Fix the TBD feature", null, null, "Title")]
    [InlineData("Good title", "This is TODO and needs work", null, "Description")]
    [InlineData("Good title", "Good desc", "We need to decide on approach", "Acceptance Criteria")]
    [InlineData("Good title", "The design is TBC", "Also ??? unclear", "Description")]
    [InlineData("Good title", "Placeholder text here", null, "Description")]
    [InlineData("Good title", "This is to be determined later", null, "Description")]
    public void DetectPlaceholders_FindsPlaceholderText(string title, string? desc, string? ac, string expectedField)
    {
        var wi = new StoryWorkItem
        {
            Id = 99, Title = title, State = "New",
            Description = desc, AcceptanceCriteria = ac,
            Tags = new List<string>()
        };

        var result = PlanningAgentService.DetectPlaceholders(wi);

        Assert.Contains("PLACEHOLDER TEXT DETECTED", result);
        Assert.Contains(expectedField, result);
    }

    [Fact]
    public void DetectPlaceholders_CleanStory_ReturnsEmpty()
    {
        var wi = new StoryWorkItem
        {
            Id = 99, Title = "Fix the footer color",
            Description = "Change the footer background to dark",
            AcceptanceCriteria = "Footer should have dark background",
            State = "New", Tags = new List<string>()
        };

        var result = PlanningAgentService.DetectPlaceholders(wi);

        Assert.Equal("", result);
    }

    [Fact]
    public void DetectPlaceholders_HtmlTags_StrippedBeforeScanning()
    {
        var wi = new StoryWorkItem
        {
            Id = 99, Title = "Story",
            Description = "<p>The approach is <b>TBD</b> pending review</p>",
            AcceptanceCriteria = "<ul><li>Done</li></ul>",
            State = "New", Tags = new List<string>()
        };

        var result = PlanningAgentService.DetectPlaceholders(wi);

        Assert.Contains("PLACEHOLDER TEXT DETECTED", result);
        Assert.Contains("Description", result);
    }

    [Fact]
    public void DetectPlaceholders_MultipleFindings_ListsAll()
    {
        var wi = new StoryWorkItem
        {
            Id = 99, Title = "TBD Feature",
            Description = "TODO: implement this. Also need to decide on DB.",
            AcceptanceCriteria = null,
            State = "New", Tags = new List<string>()
        };

        var result = PlanningAgentService.DetectPlaceholders(wi);

        // Should find TBD in title, TODO and "need to decide" in description
        Assert.Contains("Title", result);
        Assert.Contains("Description", result);
    }
}
