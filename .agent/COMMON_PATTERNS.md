# Common Patterns

> Step-by-step guides for common development tasks in this codebase.

## Adding a New Agent

1. Create service class in `src/AIAgents.Functions/Agents/`:
```csharp
public class MyNewAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly StoryTokenUsage _tokenUsage;
    private readonly ILogger<MyNewAgentService> _logger;

    public MyNewAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ILogger<MyNewAgentService> logger)
    {
        _aiClient = aiClientFactory.GetClientForAgent("MyNew");
        // ... assign fields
        _tokenUsage = new StoryTokenUsage();
    }

    public async Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken ct)
    {
        try
        {
            var story = await _adoClient.GetWorkItemAsync(task.WorkItemId, ct);
            var repoPath = await _gitOps.EnsureBranchAsync($"feature/US-{task.WorkItemId}", ct);
            var context = _contextFactory.Create(repoPath, task.WorkItemId);

            // AI call
            var result = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
                new AICompletionOptions { MaxTokens = 4096 }, ct);
            _tokenUsage.RecordUsage("MyNew", result.Usage);

            // Write output, commit, update ADO, enqueue next
            return AgentResult.Ok();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return AgentResult.Fail(ErrorCategory.Transient, "Rate limited", ex);
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(ErrorCategory.Code, ex.Message, ex);
        }
    }
}
```

2. Register in `Program.cs`:
```csharp
services.AddKeyedScoped<IAgentService, MyNewAgentService>("MyNew");
```

3. Add to `AgentType` enum:
```csharp
public enum AgentType { ..., MyNew = 7 }
```

4. Add state transition in `OrchestratorWebhook` and `AgentTaskDispatcher`

5. Create test file `src/AIAgents.Functions.Tests/Agents/MyNewAgentServiceTests.cs`

## Adding a New Configuration Option

1. Add property to the appropriate options class in `Configuration/`:
```csharp
public class AIOptions
{
    public string NewSetting { get; set; } = "default";
}
```

2. Set in Azure Function App settings with `__` separator:
```
AI__NewSetting=value
```

3. Access via DI:
```csharp
private readonly AIOptions _options;
public MyService(IOptions<AIOptions> options) => _options = options.Value;
```

## Adding a New HTTP Endpoint

1. Create function class in `src/AIAgents.Functions/Functions/`:
```csharp
public class MyEndpoint
{
    private readonly ILogger<MyEndpoint> _logger;
    public MyEndpoint(ILogger<MyEndpoint> logger) => _logger = logger;

    [Function("MyEndpoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "my-endpoint")] HttpRequestData req,
        CancellationToken ct)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { status = "ok" }, ct);
        return response;
    }
}
```

## Adding a New ADO Custom Field

1. Add field reference to `CustomFieldNames`:
```csharp
public const string MyField = "Custom.MyField";

public static class Paths
{
    public const string MyField = "/fields/Custom.MyField";
}
```

2. Add property to `StoryWorkItem`:
```csharp
public string? MyField { get; set; }
```

3. Map in `AzureDevOpsClient.MapToStoryWorkItem()`:
```csharp
MyField = GetFieldValue<string>(fields, CustomFieldNames.MyField)
```

4. Update in agents via JSON patch:
```csharp
await _adoClient.UpdateWorkItemAsync(task.WorkItemId, new Dictionary<string, object>
{
    [CustomFieldNames.Paths.MyField] = computedValue
}, ct);
```

## Writing Tests

Follow existing patterns:
```csharp
public class MyServiceTests
{
    private readonly Mock<IAIClient> _mockAI = new();
    private readonly Mock<IAzureDevOpsClient> _mockADO = new();
    private readonly Mock<IGitOperations> _mockGit = new();

    private MyService CreateService() => new(
        _mockAI.Object,
        _mockADO.Object,
        _mockGit.Object,
        Mock.Of<ILogger<MyService>>());

    [Fact]
    public async Task Method_Scenario_ExpectedResult()
    {
        // Arrange
        _mockADO.Setup(x => x.GetWorkItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MockAIResponses.SampleWorkItem());

        // Act
        var sut = CreateService();
        var result = await sut.ExecuteAsync(new AgentTask { WorkItemId = 1 }, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
    }
}
```

Key test helpers in `MockAIResponses`:
- `SampleWorkItem()` — Pre-built `StoryWorkItem` with common fields
- `SampleState()` — Pre-built `StoryState` 
- `WrapInCompletion(json)` — Wraps JSON string in `AICompletionResult`
- `ValidPlanningResponse` — Canned planning agent JSON output

## Modifying the Dashboard

The dashboard is a single file at `dashboard/index.html`. Key sections:
- **CSS** (lines 1-200): Inline styles, CSS variables for theming
- **HTML** (lines 200-600): Stat cards, agent pipeline, panels
- **JavaScript** (lines 600+): `fetch()` polling, DOM manipulation

API base URL is hardcoded:
```javascript
const API_URL = 'https://ai-agents-func-todd.azurewebsites.net/api/status';
```

Polls on intervals via `setInterval()`. Dark mode via CSS class toggle on `<body>`.
