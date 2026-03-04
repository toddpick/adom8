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
/// Tests for TestingAgentService covering test generation parsing,
/// state transitions, and error handling.
/// </summary>
public sealed class TestingAgentServiceTests
{
    private readonly Mock<IAIClientFactory> _aiFactoryMock = new();
    private readonly Mock<IAIClient> _aiClientMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IGitHubApiContextService> _githubContextMock = new();
    private readonly Mock<IStoryContextFactory> _contextFactoryMock = new();
    private readonly Mock<IStoryContext> _contextMock = new();
    private readonly Mock<ITemplateEngine> _templateMock = new();
    private readonly Mock<ICodebaseContextProvider> _codebaseMock = new();
    private readonly Mock<IAgentTaskQueue> _taskQueueMock = new();

    private StoryState _capturedState = null!;

    public TestingAgentServiceTests()
    {
        _aiFactoryMock.Setup(f => f.GetClientForAgent("Testing", It.IsAny<StoryModelOverrides?>())).Returns(_aiClientMock.Object);
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private TestingAgentService CreateService()
    {
        return new TestingAgentService(
            _aiFactoryMock.Object, _adoMock.Object, _githubContextMock.Object,
            _contextFactoryMock.Object, _templateMock.Object, _codebaseMock.Object,
            NullLogger<TestingAgentService>.Instance, _taskQueueMock.Object);
    }

    private void SetupHappyPath()
    {
        var wi = MockAIResponses.SampleWorkItem(state: "AI Test");
        var state = MockAIResponses.SampleState(wi.Id, "AI Test");
        state.Artifacts.Code.Add("src/Services/RegistrationService.cs");

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>())).ReturnsAsync(wi);
        _githubContextMock.Setup(g => g.GetFileContentsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string?> { ["src/Services/RegistrationService.cs"] = "public class RegistrationService { }" });
        _githubContextMock.Setup(g => g.WriteFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s).Returns(Task.CompletedTask);
        _contextMock.Setup(c => c.ReadArtifactAsync("PLAN.md", It.IsAny<CancellationToken>())).ReturnsAsync("Plan content");

        _aiClientMock.Setup(a => a.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult
            {
                Content = MockAIResponses.ValidTestingResponse,
                Usage = new TokenUsageData
                {
                    InputTokens = 2000, OutputTokens = 1500, TotalTokens = 3500,
                    EstimatedCost = 0.02m, Model = "gpt-4o"
                }
            });

        _templateMock.Setup(t => t.RenderAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Test Plan");

        _codebaseMock.Setup(c => c.LoadRelevantContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_GeneratesTests()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Testing });

        // Should commit test files via GitHub API
        _githubContextMock.Verify(g => g.WriteFilesAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyDictionary<string, string>>(files => files.ContainsKey("tests/RegistrationServiceTests.cs")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TransitionsToAIReview()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Testing });

        Assert.Equal("AI Review", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Testing"].Status);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TracksTokensAndArtifacts()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Testing });

        Assert.True(_capturedState.TokenUsage.Agents.ContainsKey("Testing"));
        Assert.Contains("tests/RegistrationServiceTests.cs", _capturedState.Artifacts.Tests);
    }

    [Fact]
    public async Task ExecuteAsync_WritesTestPlan()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Testing });

        _contextMock.Verify(c => c.WriteArtifactAsync("TEST_PLAN.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
