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
/// Tests for ReviewAgentService covering code review parsing,
/// score tracking, state transitions, and error handling.
/// </summary>
public sealed class ReviewAgentServiceTests
{
    private readonly Mock<IAIClientFactory> _aiFactoryMock = new();
    private readonly Mock<IAIClient> _aiClientMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IGitOperations> _gitMock = new();
    private readonly Mock<IStoryContextFactory> _contextFactoryMock = new();
    private readonly Mock<IStoryContext> _contextMock = new();
    private readonly Mock<ITemplateEngine> _templateMock = new();
    private readonly Mock<ICodebaseContextProvider> _codebaseMock = new();
    private readonly Mock<IAgentTaskQueue> _taskQueueMock = new();

    private StoryState _capturedState = null!;

    public ReviewAgentServiceTests()
    {
        _aiFactoryMock.Setup(f => f.GetClientForAgent("Review")).Returns(_aiClientMock.Object);
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private ReviewAgentService CreateService()
    {
        return new ReviewAgentService(
            _aiFactoryMock.Object, _adoMock.Object, _gitMock.Object,
            _contextFactoryMock.Object, _templateMock.Object, _codebaseMock.Object,
            NullLogger<ReviewAgentService>.Instance, _taskQueueMock.Object);
    }

    private void SetupHappyPath(string? aiResponse = null)
    {
        var wi = MockAIResponses.SampleWorkItem(state: "AI Review");
        var state = MockAIResponses.SampleState(wi.Id, "AI Review");
        state.Artifacts.Code.Add("src/Services/RegistrationService.cs");
        state.Artifacts.Tests.Add("tests/RegistrationServiceTests.cs");

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>())).ReturnsAsync(wi);
        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\repos\test");
        _gitMock.Setup(g => g.ReadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("public class Test { }");

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s).Returns(Task.CompletedTask);

        _aiClientMock.Setup(a => a.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult
            {
                Content = aiResponse ?? MockAIResponses.ValidReviewResponse(),
                Usage = new TokenUsageData
                {
                    InputTokens = 3000, OutputTokens = 1000, TotalTokens = 4000,
                    EstimatedCost = 0.02m, Model = "gpt-4o"
                }
            });

        _templateMock.Setup(t => t.RenderAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Code Review");

        _codebaseMock.Setup(c => c.LoadRelevantContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_StoresScoreInAdditionalData()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        Assert.NotNull(_capturedState.Agents["Review"].AdditionalData);
        Assert.Equal(85, _capturedState.Agents["Review"].AdditionalData!["score"]);
        Assert.Equal("Approve", _capturedState.Agents["Review"].AdditionalData["recommendation"]);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TransitionsToAIDocs()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        Assert.Equal("AI Docs", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Review"].Status);
    }

    [Fact]
    public async Task ExecuteAsync_LowScore_StillCompletes()
    {
        SetupHappyPath(aiResponse: MockAIResponses.LowScoreReviewResponse);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        Assert.Equal(40, _capturedState.Agents["Review"].AdditionalData!["score"]);
        Assert.Equal("AI Docs", _capturedState.CurrentState);
    }

    [Fact]
    public async Task ExecuteAsync_CriticalIssues_StillCompletes()
    {
        SetupHappyPath(aiResponse: MockAIResponses.ReviewWithCriticalIssues);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        Assert.Equal(30, _capturedState.Agents["Review"].AdditionalData!["score"]);
    }

    [Fact]
    public async Task ExecuteAsync_WritesCodeReviewArtifact()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        _contextMock.Verify(c => c.WriteArtifactAsync("CODE_REVIEW.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TracksTokens()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        Assert.True(_capturedState.TokenUsage.Agents.ContainsKey("Review"));
        Assert.Equal(4000, _capturedState.TokenUsage.Agents["Review"].TotalTokens);
    }
}
