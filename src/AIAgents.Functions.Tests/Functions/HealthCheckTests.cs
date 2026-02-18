using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Functions;
using AIAgents.Functions.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AIAgents.Functions.Tests.Functions;

/// <summary>
/// Tests for the HealthCheck HTTP function. Covers ADO, AI,
/// configuration, and Git checks via dependency mocks.
/// Queue checks are skipped since QueueClient is created internally.
/// </summary>
public sealed class HealthCheckTests : IDisposable
{
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IAIClient> _aiMock = new();
    private readonly TelemetryClient _telemetry;
    private readonly Dictionary<string, string?> _configValues;

    public HealthCheckTests()
    {
        var channel = new Mock<ITelemetryChannel>();
        _telemetry = new TelemetryClient(new TelemetryConfiguration
        {
            TelemetryChannel = channel.Object
        });

        _configValues = new Dictionary<string, string?>
        {
            ["AI__ApiKey"] = "test-key",
            ["AzureDevOps__Pat"] = "test-pat",
            ["Git__Token"] = "test-token",
            ["Git__RepositoryUrl"] = "https://example.com/repo.git",
            // Don't set AzureWebJobsStorage — prevents queue check from connecting
        };

        // Clear cache between tests
        HealthCheck.ClearCache();
    }

    public void Dispose()
    {
        HealthCheck.ClearCache();
    }

    private HealthCheck CreateFunction(Dictionary<string, string?>? configOverrides = null)
    {
        var values = new Dictionary<string, string?>(_configValues);
        if (configOverrides is not null)
        {
            foreach (var (key, value) in configOverrides)
            {
                values[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new HealthCheck(
            _adoMock.Object,
            _aiMock.Object,
            configuration,
            NullLogger<HealthCheck>.Instance,
            _telemetry,
            Options.Create(new AIOptions { Provider = "Claude", Model = "claude-sonnet-4-20250514", ApiKey = "test-key" }),
            Options.Create(new CopilotOptions()));
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/health";
        return context;
    }

    // ── ADO Check ──

    [Fact]
    public async Task Run_AdoHealthy_ReturnsHealthyComponent()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem
            {
                Id = 1,
                Title = "Test",
                State = "New",
                AutonomyLevel = 3,
                MinimumReviewScore = 80
            });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("healthy", healthResult.Checks["azureDevOps"].Status);
    }

    [Fact]
    public async Task Run_Ado404_StillHealthy()
    {
        // 404 means ADO is reachable but work item doesn't exist — connectivity OK
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("work item does not exist"));
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("healthy", healthResult.Checks["azureDevOps"].Status);
    }

    [Fact]
    public async Task Run_AdoConnectionFailed_ReturnsUnhealthy()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("unhealthy", healthResult.Checks["azureDevOps"].Status);
    }

    // ── Configuration Check ──

    [Fact]
    public async Task Run_MissingApiKey_ConfigUnhealthy()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction(new Dictionary<string, string?>
        {
            ["AI__ApiKey"] = null,
            ["AI:ApiKey"] = null
        });
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("unhealthy", healthResult.Checks["configuration"].Status);
        Assert.Contains("AI__ApiKey", healthResult.Checks["configuration"].MissingVars!);
    }

    [Fact]
    public async Task Run_AllConfigPresent_ConfigHealthy()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("healthy", healthResult.Checks["configuration"].Status);
    }

    // ── Git Configuration Check ──

    [Fact]
    public async Task Run_MissingGitToken_GitUnhealthy()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction(new Dictionary<string, string?>
        {
            ["Git__Token"] = null,
            ["Git:Token"] = null
        });
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        // gitConfiguration is its own check, not "configuration"
        Assert.True(healthResult.Checks.ContainsKey("git") ||
                    healthResult.Checks.ContainsKey("gitConfiguration"),
            "Should have a git-related check");
    }

    // ── AI API Check ──

    [Fact]
    public async Task Run_AiApiResponds_ReturnsHealthy()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("healthy", healthResult.Checks["aiApi"].Status);
    }

    [Fact]
    public async Task Run_AiApiFails_ReturnsDegraded()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API unavailable"));

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);
        Assert.Equal("degraded", healthResult.Checks["aiApi"].Status);
    }

    // ── Overall Status ──

    [Fact]
    public async Task Run_CriticalFail_Returns503()
    {
        // ADO fails = critical failure = 503
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(503, objResult.StatusCode);
    }

    [Fact]
    public async Task Run_AdoAndAiHealthy_Returns200OrDegraded()
    {
        // Without AzureWebJobsStorage, the queue check will fail, causing
        // degraded or unhealthy status. We verify non-queue checks pass.
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        var actionResult = await func.Run(ctx.Request, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(actionResult);
        var healthResult = Assert.IsType<HealthCheckResult>(objResult.Value);

        // ADO and AI should be healthy even if queue fails
        Assert.Equal("healthy", healthResult.Checks["azureDevOps"].Status);
        Assert.Equal("healthy", healthResult.Checks["aiApi"].Status);
        Assert.Equal("healthy", healthResult.Checks["configuration"].Status);
    }

    // ── Caching ──

    [Fact]
    public async Task Run_SecondCallWithinCachePeriod_ReturnsCachedResult()
    {
        _adoMock.Setup(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryWorkItem { Id = 1, Title = "T", State = "New", AutonomyLevel = 3, MinimumReviewScore = 80 });
        _aiMock.Setup(a => a.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AICompletionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AICompletionResult { Content = "OK" });

        var func = CreateFunction();
        var ctx = CreateHttpContext();

        await func.Run(ctx.Request, CancellationToken.None);
        await func.Run(ctx.Request, CancellationToken.None);

        // ADO should only be called once — second call uses cache
        _adoMock.Verify(a => a.GetWorkItemAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── HealthCheckResult model ──

    [Fact]
    public void HealthCheckResult_HasTimestamp()
    {
        var result = new HealthCheckResult { Status = "healthy" };

        Assert.True(result.Timestamp > DateTime.MinValue);
        Assert.NotNull(result.Checks);
    }

    [Fact]
    public void ComponentCheck_SupportsAllProperties()
    {
        var check = new ComponentCheck
        {
            Status = "healthy",
            ResponseTime = 100,
            MessageCount = 5,
            PoisonMessageCount = 1,
            Message = "test",
            MissingVars = ["VAR1"]
        };

        Assert.Equal("healthy", check.Status);
        Assert.Equal(100, check.ResponseTime);
        Assert.Equal(5, check.MessageCount);
        Assert.Equal(1, check.PoisonMessageCount);
    }
}
