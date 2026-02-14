# AIAgents.Core.Tests

Unit tests for the `AIAgents.Core` project covering template engine, AI client, story context,
token usage tracking, and model serialization.

## Test Framework

- **xUnit 2.9.2** — Test framework
- **Moq 4.20.72** — Mocking
- **coverlet.collector 6.0.2** — Code coverage

## Test Categories

| File | Tests | Coverage Area |
|------|-------|---------------|
| `Services/ScribanTemplateEngineTests.cs` | 8 | Template rendering, missing templates, all template types |
| `Services/AIClientTests.cs` | 10 | HTTP transport, auth headers, token cost calculation |
| `Services/StoryContextTests.cs` | 12 | State persistence, artifact I/O, file system operations |
| `Models/TokenUsageTests.cs` | 14 | Cost calculator, complexity classification, usage accumulation |
| `Models/ModelSerializationTests.cs` | 18 | JSON round-trip for all model classes |

**Total: 62 tests**

## Running Tests

```bash
# Run all Core tests
dotnet test src/AIAgents.Core.Tests

# Run with verbose output
dotnet test src/AIAgents.Core.Tests --verbosity normal

# Run with code coverage
dotnet test src/AIAgents.Core.Tests --collect:"XPlat Code Coverage"

# Run a specific test class
dotnet test src/AIAgents.Core.Tests --filter "FullyQualifiedName~TokenUsageTests"
```

## Key Testing Patterns

### Mock HttpMessageHandler (AIClient)
The `AIClientTests` class uses `Mock<HttpMessageHandler>` with `.Protected()` to intercept
HTTP calls made by `HttpClient`. This avoids real network calls while testing the full
request/response pipeline including headers, endpoints, and error handling.

### Temp Directory (StoryContext)
`StoryContextTests` implements `IDisposable` and creates a real temporary directory for each test.
This validates actual file I/O behavior including directory creation, JSON persistence, and
nested artifact paths. Cleanup happens automatically in `Dispose()`.

### Theory + InlineData (TokenUsage)
Token cost calculations use `[Theory]` with `[InlineData]` to verify pricing for multiple
AI models (GPT-4o, GPT-4o-mini, Claude Sonnet, Claude Opus, Claude Haiku) without
duplicating test logic.

## Coverage Goals

- **Target: 80%+** line coverage
- **Critical paths: 100%** — Token cost calculation, state persistence, template rendering
