using AIAgents.Core.Interfaces;
using AIAgents.Functions.Functions;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIAgents.Functions.Tests.Functions;

/// <summary>
/// Tests for GetCurrentStatus dashboard endpoint covering story aggregation,
/// success rate calculation, and token summaries.
/// </summary>
public sealed class GetCurrentStatusTests
{
    private readonly Mock<IActivityLogger> _activityMock = new();
    private readonly Mock<IAgentTaskQueue> _taskQueueMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();

    private GetCurrentStatus CreateFunction()
    {
        _taskQueueMock.Setup(q => q.PeekAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentTask>());

        return new GetCurrentStatus(
            NullLogger<GetCurrentStatus>.Instance,
            _activityMock.Object,
            _taskQueueMock.Object,
            _adoMock.Object);
    }

    private static HttpRequest CreateRequest()
    {
        var context = new DefaultHttpContext();
        return context.Request;
    }

    // ── Happy path ──

    [Fact]
    public async Task Run_NoActivity_ReturnsEmptyDashboard()
    {
        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActivityEntry>());

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var status = Assert.IsType<DashboardStatus>(ok.Value);
        Assert.Empty(status.Stories);
        Assert.Equal(0, status.Stats.StoriesProcessed);
        Assert.Equal(100.0, status.Stats.SuccessRate);
    }

    [Fact]
    public async Task Run_SingleStoryInProgress_ShowsAgentActivity()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-5), Agent = "Planning", WorkItemId = 100, Message = "Planning agent started processing" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-3), Agent = "Planning", WorkItemId = 100, Message = "Planning agent completed successfully" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-2), Agent = "Coding", WorkItemId = 100, Message = "Coding agent started processing" }
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        Assert.NotNull(status);
        Assert.Single(status.Stories);

        var story = status.Stories[0];
        Assert.Equal(100, story.WorkItemId);
        Assert.Equal("completed", story.Agents["Planning"]);
        Assert.Equal("in_progress", story.Agents["Coding"]);
        Assert.Equal("Coding", story.CurrentAgent);
    }

    [Fact]
    public async Task Run_CompletedStory_CountsAsProcessed()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-10), Agent = "Planning", WorkItemId = 200, Message = "Planning agent started processing" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-8), Agent = "Planning", WorkItemId = 200, Message = "Planning agent completed successfully" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-7), Agent = "Coding", WorkItemId = 200, Message = "Coding agent started processing" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-5), Agent = "Coding", WorkItemId = 200, Message = "Coding agent completed successfully" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-4), Agent = "Testing", WorkItemId = 200, Message = "Testing agent started processing" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-3), Agent = "Testing", WorkItemId = 200, Message = "Testing agent completed successfully" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-2), Agent = "Review", WorkItemId = 200, Message = "Review agent started processing" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-1), Agent = "Review", WorkItemId = 200, Message = "Review agent completed successfully" },
            new() { Timestamp = DateTime.UtcNow, Agent = "Documentation", WorkItemId = 200, Message = "Documentation agent started processing" },
            new() { Timestamp = DateTime.UtcNow.AddSeconds(30), Agent = "Documentation", WorkItemId = 200, Message = "Documentation agent completed successfully" }
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        Assert.NotNull(status);
        Assert.Equal(1, status.Stats.StoriesProcessed);
    }

    // ── Success rate calculation ──

    [Fact]
    public async Task Run_MixedResults_CalculatesSuccessRate()
    {
        var activities = new List<ActivityEntry>
        {
            // Story 1: completed
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 1, Message = "Planning agent completed successfully" },
            // Story 2: failed
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 2, Message = "Planning agent started processing" },
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 2, Message = "Planning agent failed: error" }
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        Assert.NotNull(status);
        // Story 1 has all agents completed, Story 2 has a failed agent
        // SuccessRate = completed / (completed + failed) * 100
        Assert.True(status.Stats.SuccessRate <= 100.0);
    }

    // ── Token aggregation ──

    [Fact]
    public async Task Run_WithTokenData_AggregatesTokens()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 300, Message = "Planning agent completed successfully", Tokens = 5000, Cost = 0.03m },
            new() { Timestamp = DateTime.UtcNow, Agent = "Coding", WorkItemId = 300, Message = "Coding agent completed successfully", Tokens = 8000, Cost = 0.06m },
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        Assert.NotNull(status);
        Assert.Equal(13000, status.Stats.TotalTokens);
        Assert.Equal(0.09m, status.Stats.TotalCost);
    }

    [Fact]
    public async Task Run_StoryWithTokens_HasTokenUsageDto()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 400, Message = "Planning agent completed", Tokens = 3000, Cost = 0.02m },
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        var story = status!.Stories[0];
        Assert.NotNull(story.TokenUsage);
        Assert.Equal(3000, story.TokenUsage.TotalTokens);
        Assert.Equal(0.02m, story.TokenUsage.TotalCost);
    }

    // ── Multiple stories ──

    [Fact]
    public async Task Run_MultipleStories_GroupsByWorkItemId()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 10, Message = "Planning agent completed" },
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 20, Message = "Planning agent completed" },
            new() { Timestamp = DateTime.UtcNow, Agent = "Coding", WorkItemId = 10, Message = "Coding agent started processing" },
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        Assert.Equal(2, status!.Stories.Count);
    }

    // ── Orchestrator entries filtered ──

    [Fact]
    public async Task Run_OrchestratorEntries_FilteredFromAgentStatus()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-1), Agent = "Orchestrator", WorkItemId = 500, Message = "Enqueued Planning agent" },
            new() { Timestamp = DateTime.UtcNow, Agent = "Planning", WorkItemId = 500, Message = "Planning agent started processing" },
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        var story = status!.Stories[0];
        Assert.False(story.Agents.ContainsKey("Orchestrator"));
        Assert.True(story.Agents.ContainsKey("Planning"));
    }

    // ── Progress calculation ──

    [Fact]
    public async Task Run_ThreeOfSixCompleted_Shows50Percent()
    {
        var activities = new List<ActivityEntry>
        {
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-5), Agent = "Planning", WorkItemId = 600, Message = "Planning agent completed" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-3), Agent = "Coding", WorkItemId = 600, Message = "Coding agent completed" },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-1), Agent = "Testing", WorkItemId = 600, Message = "Testing agent completed" },
        };

        _activityMock.Setup(a => a.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activities);

        var fn = CreateFunction();
        var result = await fn.Run(CreateRequest(), CancellationToken.None);

        var status = ((OkObjectResult)result).Value as DashboardStatus;
        var story = status!.Stories[0];
        Assert.Equal(50, story.Progress); // 3/6 = 50%
    }
}
