using System.Text.Json;
using AIAgents.Core.Interfaces;
using AIAgents.Functions.Functions;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using AIAgents.Functions.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIAgents.Functions.Tests.Functions;

/// <summary>
/// Tests for AgentTaskDispatcher covering deserialization, autonomy-level gating,
/// keyed DI resolution, and agent execution lifecycle.
/// </summary>
public sealed class AgentTaskDispatcherTests
{
    private readonly Mock<IActivityLogger> _activityMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IAgentService> _agentServiceMock = new();

    private AgentTaskDispatcher CreateDispatcher(int autonomyLevel = 3, bool registerAgent = true)
    {
        // Setup work item with specified autonomy level
        var wi = MockAIResponses.SampleWorkItem(autonomyLevel: autonomyLevel);
        _adoMock.Setup(a => a.GetWorkItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(wi);

        // Build service provider with keyed DI
        var services = new ServiceCollection();
        if (registerAgent)
        {
            services.AddKeyedSingleton<IAgentService>("Planning", _agentServiceMock.Object);
            services.AddKeyedSingleton<IAgentService>("Coding", _agentServiceMock.Object);
            services.AddKeyedSingleton<IAgentService>("Testing", _agentServiceMock.Object);
            services.AddKeyedSingleton<IAgentService>("Review", _agentServiceMock.Object);
            services.AddKeyedSingleton<IAgentService>("Documentation", _agentServiceMock.Object);
            services.AddKeyedSingleton<IAgentService>("Deployment", _agentServiceMock.Object);
            services.AddKeyedSingleton<IAgentService>("CodebaseDocumentation", _agentServiceMock.Object);
        }
        var provider = services.BuildServiceProvider();

        return new AgentTaskDispatcher(
            provider,
            NullLogger<AgentTaskDispatcher>.Instance,
            _activityMock.Object,
            _adoMock.Object);
    }

    private static string Serialize(AgentTask task) => JsonSerializer.Serialize(task);

    // ── ShouldSkipAgent autonomy-level tests ──

    [Fact]
    public async Task Run_Level1_OnlyPlanningRuns()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 1);
        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning });

        await dispatcher.Run(msg, CancellationToken.None);

        _agentServiceMock.Verify(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_Level1_CodingSkipped()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 1);
        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        await dispatcher.Run(msg, CancellationToken.None);

        _agentServiceMock.Verify(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_Level2_TestingRuns()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 2);
        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Testing });

        await dispatcher.Run(msg, CancellationToken.None);

        _agentServiceMock.Verify(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_Level2_ReviewSkipped()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 2);
        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Review });

        await dispatcher.Run(msg, CancellationToken.None);

        _agentServiceMock.Verify(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_Level3_AllAgentsRun()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 3);

        foreach (var agentType in new[] { AgentType.Planning, AgentType.Coding, AgentType.Testing,
            AgentType.Review, AgentType.Documentation, AgentType.Deployment })
        {
            _agentServiceMock.Invocations.Clear();
            var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = agentType });
            await dispatcher.Run(msg, CancellationToken.None);
            _agentServiceMock.Verify(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Once,
                $"{agentType} should run at autonomy level 3");
        }
    }

    [Fact]
    public async Task Run_CodebaseDocumentation_AlwaysRuns()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 1); // Even at level 1
        // WI-0 for standalone agent
        var msg = Serialize(new AgentTask { WorkItemId = 0, AgentType = AgentType.CodebaseDocumentation });

        await dispatcher.Run(msg, CancellationToken.None);

        // WorkItemId 0 skips work item lookup entirely, so agent will run
        _agentServiceMock.Verify(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Deserialization and error handling ──

    [Fact]
    public async Task Run_InvalidJson_ThrowsJsonException()
    {
        var dispatcher = CreateDispatcher();

        await Assert.ThrowsAsync<JsonException>(() =>
            dispatcher.Run("not valid json!!!", CancellationToken.None));
    }

    [Fact]
    public async Task Run_AgentFailure_Rethrows()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 3);
        _agentServiceMock.Setup(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent crashed"));

        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.Run(msg, CancellationToken.None));
    }

    [Fact]
    public async Task Run_AgentFailure_LogsErrorActivity()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 3);
        _agentServiceMock.Setup(a => a.ExecuteAsync(It.IsAny<AgentTask>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent crashed"));

        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning });

        try { await dispatcher.Run(msg, CancellationToken.None); } catch { }

        _activityMock.Verify(a => a.LogAsync(
            "Planning", 12345,
            It.Is<string>(m => m.Contains("failed")),
            "error",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Activity logging ──

    [Fact]
    public async Task Run_SuccessfulExecution_LogsStartAndComplete()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 3);
        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Planning });

        await dispatcher.Run(msg, CancellationToken.None);

        _activityMock.Verify(a => a.LogAsync(
            "Planning", 12345,
            It.Is<string>(m => m.Contains("started")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _activityMock.Verify(a => a.LogAsync(
            "Planning", 12345,
            It.Is<string>(m => m.Contains("completed")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_SkippedAgent_LogsSkipActivity()
    {
        var dispatcher = CreateDispatcher(autonomyLevel: 1);
        var msg = Serialize(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        await dispatcher.Run(msg, CancellationToken.None);

        _activityMock.Verify(a => a.LogAsync(
            It.IsAny<string>(), 12345,
            It.Is<string>(m => m.Contains("Skipped")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
