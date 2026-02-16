# Architecture

## System Architecture

```mermaid
flowchart TB
    subgraph "Azure DevOps"
        WI[Work Items]
        SH[Service Hook]
    end

    subgraph "Azure Functions (Isolated Worker)"
        WH[OrchestratorWebhook<br/>HTTP POST /api/webhook]
        ATD[AgentTaskDispatcher<br/>Queue Trigger]
        HC[HealthCheck<br/>HTTP GET /api/health]
        ES[EmergencyStop<br/>HTTP GET/POST]
        GCS[GetCurrentStatus<br/>HTTP GET /api/status]
        DLQ[DeadLetterQueueHandler<br/>Timer 15min]

        subgraph "Agent Services (Keyed DI)"
            PA[PlanningAgent]
            CA[CodingAgent]
            TA[TestingAgent]
            RA[ReviewAgent]
            DA[DocumentationAgent]
            DEP[DeploymentAgent]
        end
    end

    subgraph "External"
        AI[AI API<br/>Claude/OpenAI/etc]
        GIT[GitHub/Azure Repos]
        ASQ[Azure Storage Queue]
        AST[Azure Table Storage]
    end

    subgraph "Dashboard"
        SWA[Static Web App<br/>Vanilla JS SPA]
    end

    WI -->|State Change| SH -->|POST| WH
    WH -->|Enqueue| ASQ
    ASQ -->|Trigger| ATD
    ATD -->|Resolve by key| PA & CA & TA & RA & DA & DEP
    PA & CA & TA & RA & DA -->|HTTP| AI
    PA & CA & TA & RA & DA & DEP -->|LibGit2Sharp| GIT
    PA & CA & TA & RA & DA & DEP -->|SDK| WI
    DEP -->|Create PR| GIT
    ATD -->|Log activity| AST
    DLQ -->|Process failures| ASQ
    SWA -->|Polls| HC & ES & GCS
```

## Component Relationships

### Core Library (`AIAgents.Core`)
Shared by all consumers. Contains:
- **Interfaces** — All service contracts (`IAIClient`, `IAzureDevOpsClient`, `IGitOperations`, etc.)
- **Models** — Domain objects shared across layers
- **Configuration** — `IOptions<T>` classes for all settings sections
- **Services** — Implementations of core operations (AI, Git, ADO, templates, validation)

### Functions App (`AIAgents.Functions`)
Azure-specific hosting layer. Contains:
- **Functions** — HTTP/Queue/Timer triggers (thin entry points)
- **Agents** — Workflow implementations (contain all prompt engineering)
- **Services** — Function-layer support (activity logging, task queue)

### Dependency Flow
```
Functions → Core (project reference)
Functions.Tests → Functions + Core (project references)
Core.Tests → Core (project reference)
```

## Data Flow

### Agent Pipeline
```mermaid
sequenceDiagram
    participant ADO as Azure DevOps
    participant WH as Webhook
    participant Q as Queue
    participant D as Dispatcher
    participant Agent as Agent Service
    participant AI as AI Provider
    participant Git as Git Repo

    ADO->>WH: State change event
    WH->>Q: Enqueue AgentTask
    Q->>D: Dequeue + trigger
    D->>Agent: Execute(task, ct)
    Agent->>ADO: Fetch work item
    Agent->>Git: Ensure branch
    Agent->>Git: Load context (.agent/, .ado/)
    Agent->>AI: CompleteAsync(system, user, options)
    AI-->>Agent: Response + token usage
    Agent->>Git: Write artifacts
    Agent->>Git: Commit + force push
    Agent->>ADO: Update fields + comment
    Agent->>Q: Enqueue next agent
```

### State Management
Each story maintains state in `.ado/stories/US-{id}/state.json`:
```json
{
  "workItemId": 67,
  "currentState": "AI Code",
  "agents": {
    "Planning": { "status": "completed", "startedAt": "...", "completedAt": "..." }
  },
  "artifacts": { "codePaths": [], "testPaths": [], "docPaths": [] },
  "tokenUsage": { "agents": { "Planning": { "inputTokens": 1234, ... } } }
}
```

## Design Decisions

1. **Isolated worker model** over in-process: Better dependency isolation, .NET 8 support, independent lifecycle
2. **Queue-based dispatch** over direct HTTP calls: Automatic retry, poison queue handling, decoupled agents
3. **Keyed DI** for agent resolution: Clean dispatch without switch/case, easy to add new agents
4. **Force push on feature branches**: AI owns the entire branch — no merge conflicts, clean history
5. **Single HTTP client with resilience**: Circuit breaker + retry at transport level, not in each agent
6. **Per-agent AI model overrides**: Different models for different tasks (cheaper for docs, smarter for code)
7. **Error categorization**: Transient errors retry, config errors fail fast with clear messaging
8. **LibGit2Sharp** over CLI git: Type-safe, no shell dependency, better credential handling
9. **Scriban templates** for output formatting: Separates content from presentation for agent outputs
10. **Single-file dashboard**: No build step, no npm, instant deploy to Static Web Apps
