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

        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"C:\repos\test");
        _gitMock.Setup(g => g.ListFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "src/Program.cs", "README.md" });

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

        _adoMock.Verify(a => a.UpdateWorkItemStateAsync(12345, "AI Code", It.IsAny<CancellationToken>()), Times.Once);
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
}
