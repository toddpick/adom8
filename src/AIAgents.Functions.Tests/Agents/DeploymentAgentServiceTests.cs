using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using AIAgents.Functions.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AIAgents.Functions.Tests.Agents;

/// <summary>
/// Tests for DeploymentAgentService covering all autonomy levels,
/// review score gating, PR merge/deploy, and token summary writing.
/// </summary>
public sealed class DeploymentAgentServiceTests
{
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IRepositoryProvider> _repoMock = new();
    private readonly Mock<IGitOperations> _gitMock = new();
    private readonly Mock<IStoryContextFactory> _contextFactoryMock = new();
    private readonly Mock<IStoryContext> _contextMock = new();
    private readonly Mock<IActivityLogger> _activityMock = new();

    private StoryState _capturedState = null!;

    public DeploymentAgentServiceTests()
    {
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private DeploymentAgentService CreateService(DeploymentOptions? options = null)
    {
        var opts = Options.Create(options ?? new DeploymentOptions { PipelineName = "Deploy-To-Prod" });

        return new DeploymentAgentService(
            _adoMock.Object, _repoMock.Object, _gitMock.Object,
            _contextFactoryMock.Object, _activityMock.Object,
            opts, NullLogger<DeploymentAgentService>.Instance);
    }

    private void SetupBase(int autonomyLevel = 3, int reviewScore = 90, int prId = 42, int minScore = 85)
    {
        var wi = MockAIResponses.SampleWorkItem(state: "AI Deployment", autonomyLevel: autonomyLevel, minimumReviewScore: minScore);
        var state = MockAIResponses.CompletedPipelineState(reviewScore: reviewScore, prId: prId);

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>())).ReturnsAsync(wi);
        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\repos\test");

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s).Returns(Task.CompletedTask);

        // Allow token field writes to succeed (or silently fail)
        _adoMock.Setup(a => a.UpdateWorkItemFieldAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _adoMock.Setup(a => a.UpdateWorkItemStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _adoMock.Setup(a => a.AddWorkItemCommentAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repoMock.Setup(r => r.MergePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoMock.Setup(r => r.TriggerDeploymentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(101);
    }

    // ── Autonomy Level 3: Pause for human review ──

    [Fact]
    public async Task ExecuteAsync_Level3_SetsStateToCodeReview()
    {
        SetupBase(autonomyLevel: 3, reviewScore: 90);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        Assert.Equal("Code Review", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Deployment"].Status);
    }

    [Fact]
    public async Task ExecuteAsync_Level3_DoesNotMergePR()
    {
        SetupBase(autonomyLevel: 3, reviewScore: 90);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        _repoMock.Verify(r => r.MergePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Level2_AlsoSetsCodeReview()
    {
        SetupBase(autonomyLevel: 2, reviewScore: 90);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        Assert.Equal("Code Review", _capturedState.CurrentState);
    }

    // ── Autonomy Level 4: Auto-merge ──

    [Fact]
    public async Task ExecuteAsync_Level4_HighScore_MergesPR()
    {
        SetupBase(autonomyLevel: 4, reviewScore: 90, prId: 42);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        _repoMock.Verify(r => r.MergePullRequestAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Ready for Deployment", _capturedState.CurrentState);
    }

    [Fact]
    public async Task ExecuteAsync_Level4_LowScore_NeedsRevision()
    {
        SetupBase(autonomyLevel: 4, reviewScore: 50, minScore: 85);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        Assert.Equal("Needs Revision", _capturedState.CurrentState);
        _repoMock.Verify(r => r.MergePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Level4_NoPrId_SkipsMerge()
    {
        SetupBase(autonomyLevel: 4, reviewScore: 90, prId: 0);
        // Override state to not have PR ID
        var state = MockAIResponses.SampleState(12345, "AI Deployment");
        state.Agents["Review"] = AgentStatus.Completed();
        state.Agents["Review"].AdditionalData = new Dictionary<string, object> { ["score"] = 90 };
        // No Documentation agent data with prId
        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);

        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        Assert.Equal("Ready for Deployment", _capturedState.CurrentState);
        _repoMock.Verify(r => r.MergePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Autonomy Level 5: Auto-merge + deploy ──

    [Fact]
    public async Task ExecuteAsync_Level5_HighScore_MergesAndDeploys()
    {
        SetupBase(autonomyLevel: 5, reviewScore: 90, prId: 42);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        _repoMock.Verify(r => r.MergePullRequestAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        _repoMock.Verify(r => r.TriggerDeploymentAsync("main", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Deployed", _capturedState.CurrentState);
    }

    [Fact]
    public async Task ExecuteAsync_Level5_LowScore_NeedsRevision()
    {
        SetupBase(autonomyLevel: 5, reviewScore: 60, minScore: 85);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        Assert.Equal("Needs Revision", _capturedState.CurrentState);
        _repoMock.Verify(r => r.MergePullRequestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Level5_NoPipeline_MergesButSkipsDeploy()
    {
        SetupBase(autonomyLevel: 5, reviewScore: 90, prId: 42);
        _repoMock.Setup(r => r.TriggerDeploymentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No pipeline configured"));
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        _repoMock.Verify(r => r.MergePullRequestAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Ready for Deployment", _capturedState.CurrentState);
    }

    // ── Token field writes ──

    [Fact]
    public async Task ExecuteAsync_WritesTokenFieldsToADO()
    {
        SetupBase(autonomyLevel: 3, reviewScore: 90);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        _adoMock.Verify(a => a.UpdateWorkItemFieldAsync(12345, "/fields/Custom.AITokensUsed", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        _adoMock.Verify(a => a.UpdateWorkItemFieldAsync(12345, "/fields/Custom.AICost", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        _adoMock.Verify(a => a.UpdateWorkItemFieldAsync(12345, "/fields/Custom.AIComplexity", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TokenFieldWriteFailure_DoesNotThrow()
    {
        SetupBase(autonomyLevel: 3, reviewScore: 90);
        _adoMock.Setup(a => a.UpdateWorkItemFieldAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Custom field not found"));
        var service = CreateService();

        // Should not throw — warning is logged
        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        Assert.Equal("Code Review", _capturedState.CurrentState);
    }

    // ── Activity logging ──

    [Fact]
    public async Task ExecuteAsync_LogsActivity()
    {
        SetupBase(autonomyLevel: 3, reviewScore: 90);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        _activityMock.Verify(a => a.LogAsync(
            "Deployment", 12345, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Decision tracking ──

    [Fact]
    public async Task ExecuteAsync_StoresDecisionInAdditionalData()
    {
        SetupBase(autonomyLevel: 4, reviewScore: 90, prId: 42);
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Deployment });

        var data = _capturedState.Agents["Deployment"].AdditionalData!;
        Assert.Equal(4, data["autonomyLevel"]);
        Assert.Equal(90, data["reviewScore"]);
        Assert.NotNull(data["decision"]);
        Assert.NotNull(data["reason"]);
    }
}
