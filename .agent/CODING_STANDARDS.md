# Coding Standards

> Extracted from actual codebase patterns. Follow these conventions for all changes.

## Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Classes | PascalCase | `CodingAgentService`, `StoryWorkItem` |
| Interfaces | `I` prefix + PascalCase | `IAIClient`, `IGitOperations` |
| Methods | PascalCase + Async suffix | `CompleteAsync()`, `FetchWorkItemAsync()` |
| Properties | PascalCase | `WorkItemId`, `MaxTokens` |
| Private fields | `_camelCase` | `_logger`, `_aiClient`, `_options` |
| Parameters | camelCase | `workItemId`, `cancellationToken` |
| Constants | PascalCase | `CustomFieldNames.AutonomyLevel` |
| Test methods | `MethodName_Scenario_ExpectedResult` | `CompleteAsync_ValidResponse_ReturnsContent` |
| Config sections | PascalCase matching class | `"AI"` → `AIOptions`, `"Git"` → `GitOptions` |

## File Organization

- **One class per file** (with minor exceptions for small related types)
- **Folder = namespace**: `AIAgents.Core.Services`, `AIAgents.Functions.Agents`
- **Interfaces** in `Interfaces/` folder, implementations in `Services/`
- **Models** in `Models/` folder, one file per model
- **Configuration** classes in `Configuration/` folder
- **Test files** mirror source structure: `Services/AIClientTests.cs` tests `Services/AIClient.cs`

## Dependency Injection

All services use constructor injection:
```csharp
public class CodingAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly ILogger<CodingAgentService> _logger;

    public CodingAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        ILogger<CodingAgentService> logger)
    {
        _aiClient = aiClientFactory.GetClientForAgent("Coding");
        _adoClient = adoClient;
        _gitOps = gitOps;
        _logger = logger;
    }
}
```

Registration pattern in `Program.cs`:
- Core services: `AddSingleton<TInterface, TImpl>()`
- Agents: `AddKeyedScoped<IAgentService, TAgent>("AgentName")`
- Config: `Configure<T>(configuration.GetSection("SectionName"))`

## Error Handling

Agents use categorized errors:
```csharp
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
{
    return AgentResult.Fail(ErrorCategory.Transient, "Rate limited", ex);
}
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
{
    return AgentResult.Fail(ErrorCategory.Configuration, "Auth failed", ex);
}
catch (Exception ex)
{
    return AgentResult.Fail(ErrorCategory.Code, $"Unexpected: {ex.Message}", ex);
}
```

- **Transient** → Dispatcher throws → Queue retries (max 2 dequeues)
- **Configuration/Data** → Permanent failure → Comment on ADO, set "Agent Failed" state
- **Code** → Throw → Retry once, then dead-letter queue

## Logging

Structured logging with Application Insights:
```csharp
_logger.LogInformation("Planning agent starting for WI-{WorkItemId}", task.WorkItemId);
_logger.LogDebug("Completion received: {CharCount} characters", content.Length);
_logger.LogWarning(ex, "Failed to parse token usage — continuing");
_logger.LogError(ex, "No agent registered for key '{AgentKey}'", agentKey);
```

Rules:
- Use **structured parameters** (not string interpolation) in log templates
- `LogInformation` for business events (agent start/complete, state transitions)
- `LogDebug` for technical detail (response sizes, file counts)
- `LogWarning` for recoverable issues (parse failures, missing optional data)
- `LogError` for failures requiring attention
- Use `TelemetryClient.TrackEvent()` for custom metrics

## AI Response Parsing

All agents follow this pattern:
1. Request JSON in the system prompt
2. Strip markdown code fences from response (````json\n...\n```)
3. Deserialize with `JsonSerializer.Deserialize<T>()`
4. Handle deserialization failures gracefully (log warning, use fallback)

```csharp
var content = aiResult.Content.Trim();
if (content.StartsWith("```"))
{
    content = content.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate((a, b) => a + "\n" + b);
}
var result = JsonSerializer.Deserialize<PlanningResult>(content, _jsonOptions);
```

## Async Patterns

- All I/O methods are async with `CancellationToken ct` parameter
- Use `ConfigureAwait(false)` is NOT used (Azure Functions isolated worker handles context correctly)
- Name async methods with `Async` suffix
- Pass `CancellationToken` through entire call chain

## Null Handling

- Nullable reference types enabled project-wide (`<Nullable>enable</Nullable>`)
- Use `??` and `?.` operators for null coalescing
- Validate required constructor parameters: no explicit ArgumentNullException — rely on nullable warnings
- Optional config uses nullable properties: `string? Endpoint`, `int? MaxTokens`
