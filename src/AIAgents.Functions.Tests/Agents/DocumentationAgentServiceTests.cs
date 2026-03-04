using AIAgents.Core.Interfaces;
using AIAgents.Core.Constants;
using AIAgents.Core.Models;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using AIAgents.Functions.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIAgents.Functions.Tests.Agents;

/// <summary>
/// Tests for DocumentationAgentService covering doc generation, PR creation,
/// state transitions, and error handling.
/// </summary>
public sealed class DocumentationAgentServiceTests
{
    private readonly Mock<IAIClientFactory> _aiFactoryMock = new();
    private readonly Mock<IAIClient> _aiClientMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IRepositoryProvider> _repoMock = new();
    private readonly Mock<IGitHubApiContextService> _githubContextMock = new();
    private readonly Mock<IStoryContextFactory> _contextFactoryMock = new();
    private readonly Mock<IStoryContext> _contextMock = new();
    private readonly Mock<ITemplateEngine> _templateMock = new();
    private readonly Mock<ICodebaseContextProvider> _codebaseMock = new();
    private readonly Mock<IAgentTaskQueue> _taskQueueMock = new();

    private StoryState _capturedState = null!;

    public DocumentationAgentServiceTests()
    {
        _aiFactoryMock.Setup(f => f.GetClientForAgent("Documentation", It.IsAny<StoryModelOverrides?>())).Returns(_aiClientMock.Object);
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private DocumentationAgentService CreateService()
    {
        return new DocumentationAgentService(
            _aiFactoryMock.Object, _adoMock.Object, _repoMock.Object,
            _githubContextMock.Object, _contextFactoryMock.Object, _templateMock.Object,
            _codebaseMock.Object, NullLogger<DocumentationAgentService>.Instance,
            _taskQueueMock.Object);
    }

    private void SetupHappyPath()
    {
        var wi = MockAIResponses.SampleWorkItem(state: "AI Docs");
        var state = MockAIResponses.SampleState(wi.Id, "AI Docs");
        state.Artifacts.Code.Add("src/Services/RegistrationService.cs");
        state.Artifacts.Tests.Add("tests/RegistrationServiceTests.cs");

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>())).ReturnsAsync(wi);
        _githubContextMock.Setup(g => g.GetFileContentsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string?> { ["src/Services/RegistrationService.cs"] = "public class Test { }" });
        _githubContextMock.Setup(g => g.WriteFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        _contextMock.Setup(c => c.ReadArtifactAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("# Plan Content");
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s).Returns(Task.CompletedTask);

        _aiClientMock.Setup(a => a.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult
            {
                Content = MockAIResponses.ValidDocumentationResponse,
                Usage = new TokenUsageData
                {
                    InputTokens = 5000, OutputTokens = 2000, TotalTokens = 7000,
                    EstimatedCost = 0.04m, Model = "gpt-4o"
                }
            });

        _templateMock.Setup(t => t.RenderAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Documentation");

        _codebaseMock.Setup(c => c.LoadRelevantContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        _repoMock.Setup(r => r.CreatePullRequestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_CreatesPullRequest()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        _repoMock.Verify(r => r.CreatePullRequestAsync(
            "feature/US-12345", "main",
            It.Is<string>(t => t.Contains("12345")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_StoresPrIdInAdditionalData()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        Assert.Equal(42, _capturedState.Agents["Documentation"].AdditionalData!["prId"]);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_TransitionsToAIDeployment()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        Assert.Equal("AI Deployment", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Documentation"].Status);
        _adoMock.Verify(a => a.UpdateWorkItemFieldAsync(12345,
            CustomFieldNames.Paths.CurrentAIAgent,
            AIPipelineNames.CurrentAgentValues.Deployment,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WritesDocumentationArtifact()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        _contextMock.Verify(c => c.WriteArtifactAsync("DOCUMENTATION.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TracksTokenUsage()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        Assert.True(_capturedState.TokenUsage.Agents.ContainsKey("Documentation"));
        Assert.Equal(7000, _capturedState.TokenUsage.Agents["Documentation"].TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_CommitsWithAIDocsPrefix()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        _githubContextMock.Verify(g => g.WriteFilesAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.Is<string>(m => m.Contains("[AI Docs]") && m.Contains("12345")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TracksDocArtifactPath()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        Assert.Contains(_capturedState.Artifacts.Docs,
            d => d.Contains("DOCUMENTATION.md"));
    }

    [Fact]
    public async Task ExecuteAsync_PostsDetailedDocumentationSummaryComment()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Documentation });

        _adoMock.Verify(a => a.AddWorkItemCommentAsync(12345,
            It.Is<string>(c => c.Contains("AI Documentation Agent Complete") &&
                              c.Contains("Documentation artifact") &&
                              c.Contains("Code files summarized") &&
                              c.Contains("Test files summarized") &&
                              c.Contains("Sections generated")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
