using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Models;
using AIAgents.Functions.Tests.Helpers;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIAgents.Functions.Tests.Agents;

/// <summary>
/// Tests for CodingAgentService covering code generation parsing,
/// file creation, state transitions, and error handling.
/// </summary>
public sealed class CodingAgentServiceTests
{
    private readonly Mock<IAIClientFactory> _aiFactoryMock = new();
    private readonly Mock<IAIClient> _aiClientMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IGitOperations> _gitMock = new();
    private readonly Mock<IStoryContextFactory> _contextFactoryMock = new();
    private readonly Mock<IStoryContext> _contextMock = new();
    private readonly Mock<ICodebaseContextProvider> _codebaseMock = new();
    private readonly Mock<IAgentTaskQueue> _taskQueueMock = new();

    private StoryState _capturedState = null!;

    public CodingAgentServiceTests()
    {
        _aiFactoryMock.Setup(f => f.GetClientForAgent("Coding", It.IsAny<StoryModelOverrides?>())).Returns(_aiClientMock.Object);
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private CodingAgentService CreateService()
    {
        return new CodingAgentService(
            _aiFactoryMock.Object, _adoMock.Object, _gitMock.Object,
            _contextFactoryMock.Object, _codebaseMock.Object,
            NullLogger<CodingAgentService>.Instance, _taskQueueMock.Object);
    }

    private void SetupHappyPath(string? aiResponse = null)
    {
        var wi = MockAIResponses.SampleWorkItem();
        var state = MockAIResponses.SampleState(wi.Id, "AI Code");

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>())).ReturnsAsync(wi);
        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\repos\test");
        _gitMock.Setup(g => g.ListFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "src/Program.cs" });

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s).Returns(Task.CompletedTask);
        _contextMock.Setup(c => c.ReadArtifactAsync("PLAN.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Plan\nSome plan content.");

        _aiClientMock.Setup(a => a.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult
            {
                Content = aiResponse ?? MockAIResponses.ValidCodingResponse,
                Usage = new TokenUsageData
                {
                    InputTokens = 1500, OutputTokens = 3000, TotalTokens = 4500,
                    EstimatedCost = 0.03m, Model = "gpt-4o"
                }
            });

        _codebaseMock.Setup(c => c.LoadRelevantContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_GeneratesCodeFiles()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        // Should write 2 files (from ValidCodingResponse)
        _gitMock.Verify(g => g.WriteFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TracksArtifacts()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal(2, _capturedState.Artifacts.Code.Count);
        Assert.Contains("src/Services/RegistrationService.cs", _capturedState.Artifacts.Code);
        Assert.Contains("src/Models/User.cs", _capturedState.Artifacts.Code);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TransitionsToAITest()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal("AI Test", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Coding"].Status);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TracksTokens()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.True(_capturedState.TokenUsage.Agents.ContainsKey("Coding"));
        Assert.Equal(4500, _capturedState.TokenUsage.Agents["Coding"].TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedResponse_SavesAsRawFile()
    {
        SetupHappyPath(aiResponse: MockAIResponses.MalformedCodingResponse);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        // Malformed: saved as single raw file
        _gitMock.Verify(g => g.WriteFileAsync(
            It.IsAny<string>(), "ai-generated-code.txt", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoPlan_StillRuns()
    {
        SetupHappyPath();
        _contextMock.Setup(c => c.ReadArtifactAsync("PLAN.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = CreateService();
        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        // Should use fallback text
        Assert.Equal("AI Test", _capturedState.CurrentState);
    }
}
