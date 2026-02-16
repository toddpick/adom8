# Tech Stack

## Languages & Frameworks

| Technology | Version | Usage |
|-----------|---------|-------|
| C# | 12 | Primary language, nullable enabled |
| .NET | 8.0 | Target framework for all projects |
| Azure Functions | v4 (isolated worker) | Serverless hosting |
| xUnit | 2.9.2 | Unit testing |
| Moq | 4.20.72 | Mocking framework |
| Scriban | latest | Template engine for markdown output |
| LibGit2Sharp | latest | Git operations (clone, branch, commit, push) |
| Terraform | ~> 4.14 | Infrastructure as Code |

## NuGet Dependencies

### AIAgents.Core
- `LibGit2Sharp` — Native Git operations
- `Scriban` — Liquid-compatible template engine
- `Microsoft.TeamFoundationServer.Client` — Azure DevOps SDK
- `Microsoft.VisualStudio.Services.Client` — Azure DevOps auth
- `System.Text.Json` — JSON serialization
- `Microsoft.Extensions.Logging.Abstractions` — Structured logging
- `Microsoft.Extensions.Options` — IOptions<T> pattern
- `Microsoft.Extensions.Http` — IHttpClientFactory

### AIAgents.Functions
- `Microsoft.Azure.Functions.Worker` — Isolated worker model
- `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` — Queue triggers
- `Microsoft.Azure.Functions.Worker.Extensions.Timer` — Timer triggers
- `Microsoft.Azure.Functions.Worker.Extensions.Http` — HTTP triggers
- `Azure.Data.Tables` — Activity log storage
- `Microsoft.Extensions.Http.Resilience` — Polly-based resilience (retry, circuit breaker, timeout)
- `Microsoft.ApplicationInsights.WorkerService` — Telemetry

### Test Projects
- `Microsoft.NET.Test.Sdk` 17.11.1
- `xunit` 2.9.2
- `xunit.runner.visualstudio` 2.8.2
- `Moq` 4.20.72
- `coverlet.collector` 6.0.2

## Azure Services

| Service | SKU | Purpose |
|---------|-----|---------|
| Azure Functions | Consumption (Y1) | Agent execution |
| Azure Storage | Standard LRS | Queues, Tables, Blob (temp repos) |
| Application Insights | Standard | Monitoring, telemetry |
| Static Web App | Free | Dashboard hosting |

## AI Providers (Supported)

| Provider | API Style | Auth Header |
|----------|-----------|-------------|
| Claude (Anthropic) | Messages API | `x-api-key` + `anthropic-version` |
| OpenAI | Chat Completions | `Authorization: Bearer` |
| Azure OpenAI | Chat Completions | `api-key` header |
| Google (Gemini) | OpenAI-compatible | `Authorization: Bearer` |
| OpenRouter | OpenAI-compatible | `Authorization: Bearer` |

Provider auto-detection works by model name prefix (`claude-` → Claude, `gpt-` → OpenAI, `gemini-` → Google, etc.).

## Build & Run

```bash
# Build
dotnet build src/AIAgents.sln

# Test
dotnet test src/AIAgents.sln

# Publish
dotnet publish src/AIAgents.Functions/AIAgents.Functions.csproj -c Release -o ./publish

# Deploy
Compress-Archive -Path ./publish/* -DestinationPath ./publish.zip -Force
az functionapp deployment source config-zip --name <app-name> --resource-group <rg> --src ./publish.zip
```
