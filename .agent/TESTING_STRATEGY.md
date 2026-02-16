# Testing Strategy

## Framework

| Tool | Version | Purpose |
|------|---------|---------|
| xUnit | 2.9.2 | Test framework |
| Moq | 4.20.72 | Mocking |
| Microsoft.NET.Test.Sdk | 17.11.1 | Test runner |
| coverlet.collector | 6.0.2 | Code coverage |

## Test Structure

```
src/
├── AIAgents.Core.Tests/           # 8 test files
│   ├── Services/
│   │   ├── AIClientTests.cs       # HTTP transport, response parsing
│   │   ├── AIClientFactoryTests.cs # Model resolution chain
│   │   ├── AutonomyLevelParsingTests.cs # Picklist + number parsing
│   │   ├── InputValidatorTests.cs  # Prompt injection, HTML, length
│   │   ├── ScribanTemplateEngineTests.cs # Template rendering
│   │   └── StoryContextTests.cs    # State load/save
│   └── Models/
│       ├── ModelSerializationTests.cs # JSON round-trip
│       └── TokenUsageTests.cs       # Token counting, cost calc
├── AIAgents.Functions.Tests/      # 12 test files
│   ├── Agents/
│   │   ├── PlanningAgentServiceTests.cs
│   │   ├── CodingAgentServiceTests.cs
│   │   ├── TestingAgentServiceTests.cs
│   │   ├── ReviewAgentServiceTests.cs
│   │   ├── DocumentationAgentServiceTests.cs
│   │   └── DeploymentAgentServiceTests.cs
│   ├── Functions/
│   │   ├── AgentTaskDispatcherTests.cs  # Dispatch + error handling
│   │   ├── HealthCheckTests.cs
│   │   ├── GetCurrentStatusTests.cs
│   │   ├── DeadLetterQueueHandlerTests.cs
│   │   └── WebhookPayloadParsingTests.cs
│   ├── Models/
│   │   └── AgentResultTests.cs
│   └── Helpers/
│       └── MockAIResponses.cs     # Shared test fixtures
```

## Naming Convention

```
MethodName_Scenario_ExpectedResult
```

Examples:
- `CompleteAsync_ValidResponse_ReturnsContentAndUsage`
- `Run_Level1_OnlyPlanningRuns`
- `Execute_TransientError_ReturnsTransientCategory`
- `ParseAutonomyLevel_PicklistString_ExtractsNumber`

## Test Patterns

### Standard Unit Test
```csharp
[Fact]
public async Task Method_Scenario_ExpectedResult()
{
    // Arrange - set up mocks
    _mockDep.Setup(x => x.DoSomething(It.IsAny<int>()))
        .ReturnsAsync(expectedValue);

    // Act
    var sut = CreateService();
    var result = await sut.ExecuteAsync(input, CancellationToken.None);

    // Assert
    Assert.True(result.Success);
    Assert.Equal(expected, result.Value);
}
```

### Theory (Parameterized)
```csharp
[Theory]
[InlineData("3 - Review & Pause", 3)]
[InlineData("1 - Plan Only", 1)]
[InlineData("5", 5)]
[InlineData(3.0, 3)]
public void ParseAutonomyLevel_ValidInput_ReturnsExpected(object input, int expected)
{
    var result = AzureDevOpsClient.ParseAutonomyLevel(input);
    Assert.Equal(expected, result);
}
```

### Mocking AI Responses
```csharp
_mockAI.Setup(x => x.CompleteAsync(
    It.IsAny<string>(), It.IsAny<string>(),
    It.IsAny<AICompletionOptions>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(MockAIResponses.WrapInCompletion(validJson));
```

### State Capture
```csharp
StoryState? captured = null;
_mockContext.Setup(x => x.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
    .Callback<StoryState, CancellationToken>((s, _) => captured = s);

// ... execute ...

Assert.Equal("completed", captured!.Agents["Planning"].Status);
```

### Keyed DI Testing
```csharp
var services = new ServiceCollection();
services.AddKeyedSingleton<IAgentService>("Planning", mockPlanning.Object);
var provider = services.BuildServiceProvider();
var dispatcher = new AgentTaskDispatcher(provider, ...);
```

## Running Tests

```bash
# All tests
dotnet test src/AIAgents.sln

# Specific project
dotnet test src/AIAgents.Core.Tests/

# With coverage
dotnet test src/AIAgents.sln --collect:"XPlat Code Coverage"

# Specific test
dotnet test --filter "ClassName=AIClientTests"
```

## Coverage Focus

- **Agent happy paths**: Each agent has basic success flow tests
- **Error categorization**: Dispatcher behavior per ErrorCategory
- **AI client transport**: Both Claude and OpenAI API formats
- **Model resolution chain**: AIClientFactory's 4-level override priority
- **Input validation**: Prompt injection detection, HTML sanitization
- **Webhook parsing**: ADO service hook payload deserialization
- **State transitions**: Work item state machine progression
