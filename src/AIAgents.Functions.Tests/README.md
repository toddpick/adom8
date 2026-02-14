# AIAgents.Functions.Tests

Unit tests for the `AIAgents.Functions` project covering all six agent services,
task dispatcher, webhook parsing, and dashboard status aggregation.

## Test Framework

- **xUnit 2.9.2** — Test framework
- **Moq 4.20.72** — Mocking
- **coverlet.collector 6.0.2** — Code coverage

## Test Categories

### Agent Service Tests

| File | Tests | Coverage Area |
|------|-------|---------------|
| `Agents/PlanningAgentServiceTests.cs` | 12 | Plan generation, artifact writing, state transitions, error handling |
| `Agents/CodingAgentServiceTests.cs` | 6 | Code file parsing, artifact tracking, malformed response fallback |
| `Agents/TestingAgentServiceTests.cs` | 4 | Test generation, state transitions, test plan writing |
| `Agents/ReviewAgentServiceTests.cs` | 6 | Review scoring, low score paths, critical issues |
| `Agents/DocumentationAgentServiceTests.cs` | 7 | PR creation, doc artifacts, token tracking |
| `Agents/DeploymentAgentServiceTests.cs` | 14 | All autonomy levels (1-5), score gating, merge/deploy decisions |

### Function Tests

| File | Tests | Coverage Area |
|------|-------|---------------|
| `Functions/AgentTaskDispatcherTests.cs` | 12 | Autonomy-level gating, keyed DI, error propagation |
| `Functions/WebhookPayloadParsingTests.cs` | 12 | Payload deserialization, state mapping, work item ID extraction |
| `Functions/GetCurrentStatusTests.cs` | 9 | Dashboard aggregation, success rate, token summaries |

### Helpers

| File | Purpose |
|------|---------|
| `Helpers/MockAIResponses.cs` | Canned AI responses, sample work items, sample states |

**Total: 82 tests**

## Running Tests

```bash
# Run all Functions tests
dotnet test src/AIAgents.Functions.Tests

# Run with verbose output
dotnet test src/AIAgents.Functions.Tests --verbosity normal

# Run with code coverage
dotnet test src/AIAgents.Functions.Tests --collect:"XPlat Code Coverage"

# Run only agent tests
dotnet test src/AIAgents.Functions.Tests --filter "FullyQualifiedName~Agents"

# Run only deployment autonomy tests
dotnet test src/AIAgents.Functions.Tests --filter "FullyQualifiedName~DeploymentAgentServiceTests"
```

## Key Testing Patterns

### Agent Service Setup
Each agent test class follows a consistent pattern:
1. **`SetupHappyPath()`** — Configures all mock dependencies for the default success scenario
2. **`CreateService()`** — Constructs the SUT with mocked dependencies
3. **`_capturedState`** — Captures the last `StoryState` passed to `SaveStateAsync` for assertions

### Mock IAIClientFactory
Agent services receive `IAIClientFactory` (not `IAIClient` directly). Tests mock the factory:
```csharp
_aiFactoryMock.Setup(f => f.GetClientForAgent("Planning")).Returns(_aiClientMock.Object);
```

### InMemoryCollection IConfiguration
Agent services that need configuration use:
```csharp
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true" })
    .Build();
```

### Autonomy Level Testing (DeploymentAgent)
The `DeploymentAgentServiceTests` class covers all five autonomy levels:
- **Level ≤3** → "Code Review" (human approval required)
- **Level 4 + high score** → Auto-merge PR → "Ready for Deployment"
- **Level 4 + low score** → "Needs Revision"
- **Level 5 + high score** → Merge + Deploy → "Deployed"
- **Level 5 + no pipeline** → Merge only → "Ready for Deployment"

### OrchestratorWebhook Note
`OrchestratorWebhook` creates a real `QueueClient` in its constructor, making it difficult
to unit test without Azure Storage/Azurite. The `WebhookPayloadParsingTests` class tests the
webhook parsing and state mapping logic independently. For full integration testing, ensure
Azurite is running locally.

## Coverage Goals

- **Target: 70%+** line coverage
- **Critical paths: 100%** — Autonomy level decisions, state transitions, token tracking
