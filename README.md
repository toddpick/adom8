# AI Development Agents

AI-powered development workflow automation for Azure DevOps. When a work item's state changes, autonomous AI agents analyze, code, test, review, and document the work — pushing changes to Git and advancing work items through the pipeline automatically.

## Architecture

```
Azure DevOps Service Hook (work item state change)
  → OrchestratorWebhook (HTTP Function — receives, queues, returns immediately)
    → "agent-tasks" Azure Storage Queue
      → AgentTaskDispatcher (Queue Function — single consumer)
        → Keyed IAgentService resolution (.NET 8 keyed DI)
          ├── PlanningAgent   → Analyzes story, creates technical plan
          ├── CodingAgent     → Generates code from plan
          ├── TestingAgent    → Generates unit tests
          ├── ReviewAgent     → Reviews code, assigns quality score
          └── DocsAgent       → Generates documentation
```

**State Machine:** `Story Planning` → `AI Code` → `AI Test` → `AI Review` → `AI Docs` → `Ready for QA`

**Key Design Choices:**
- **Queue-based** — No HTTP timeouts, infinite scalability, automatic retry with poison queue
- **Single dispatcher** — Azure Storage Queues have no message filtering; one function dispatches via keyed DI
- **Thin AI client** — `IAIClient.CompleteAsync()` only; agents own their prompt engineering
- **Multi-provider** — Claude, OpenAI, or Azure OpenAI via configuration

## Quick Start

### Prerequisites

- Azure subscription
- Azure DevOps organization with a project
- Claude or OpenAI API key
- [Terraform](https://www.terraform.io/downloads) ≥ 1.0
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local) v4

### Deploy

```bash
# 1. Infrastructure
cd infrastructure
cp terraform.tfvars.example terraform.tfvars  # Edit with your values
terraform init
terraform apply

# 2. Configure secrets (from terraform output)
az functionapp config appsettings set \
  --name <function-app-name> \
  --resource-group <rg-name> \
  --settings \
    "AI__ApiKey=YOUR_KEY" \
    "AzureDevOps__OrganizationUrl=https://dev.azure.com/yourorg" \
    "AzureDevOps__Pat=YOUR_PAT" \
    "AzureDevOps__Project=YourProject" \
    "Git__RepositoryUrl=YOUR_REPO_URL" \
    "Git__Token=YOUR_GIT_PAT"

# 3. Deploy Functions
cd src/AIAgents.Functions
func azure functionapp publish <function-app-name>

# 4. Deploy Dashboard (via GitHub Actions or manual)
# See .github/workflows/deploy-dashboard.yml

# 5. Configure Azure DevOps Service Hook
# Project Settings → Service Hooks → Web Hooks
# Trigger: Work item updated (state field changes)
# URL: https://<function-app>.azurewebsites.net/api/OrchestratorWebhook
```

See [SETUP.md](SETUP.md) for detailed instructions and [DEMO_GUIDE.md](DEMO_GUIDE.md) for the live demo script.

## Configuration

| Variable | Description | Example |
|----------|-------------|---------|
| `AI__Provider` | AI provider: `Claude`, `OpenAI`, `AzureOpenAI` | `Claude` |
| `AI__Model` | Model name | `claude-sonnet-4-20250514` |
| `AI__ApiKey` | API key for AI provider | |
| `AI__Endpoint` | Azure OpenAI endpoint (optional) | `https://myai.openai.azure.com` |
| `AI__MaxTokens` | Max response tokens | `4096` |
| `AzureDevOps__OrganizationUrl` | ADO org URL | `https://dev.azure.com/myorg` |
| `AzureDevOps__Pat` | Personal access token | |
| `AzureDevOps__Project` | Project name | `MyProject` |
| `Git__RepositoryUrl` | Git repo URL | `https://dev.azure.com/org/proj/_git/repo` |
| `Git__Username` | Git username | `ai-agent-bot` |
| `Git__Token` | Git PAT | |
| `Git__Email` | Git commit email | `ai-agents@example.com` |
| `Git__Name` | Git commit name | `AI Agent Bot` |

## Project Structure

```
ai-agents/
├── infrastructure/          # Terraform (Azure resources)
├── src/
│   ├── AIAgents.Core/       # Shared library (AI clients, Git, ADO, templates)
│   └── AIAgents.Functions/  # Azure Functions (agents, API, webhook)
├── dashboard/               # Static HTML/JS monitoring dashboard
├── .github/workflows/       # CI/CD (GitHub Actions)
├── .ado/templates/          # Markdown templates for story workspaces
└── .planning/               # Build tracking and session handoff
```

## Local Development

```bash
# Restore and build
cd src
dotnet restore
dotnet build

# Run Functions locally (requires Azurite for queue emulation)
cd AIAgents.Functions
func start

# Dashboard - open dashboard/index.html in browser
# Update API_URL in the script to http://localhost:7071
```

## For Developers

This codebase uses AI agents for development automation. To work effectively:

1. **Read the developer guide:** [DEVELOPERS.md](DEVELOPERS.md)
2. **Review .agent/ documentation:** [.agent/README.md](.agent/README.md)
3. **Use helper scripts:** [scripts/README.md](scripts/README.md)

### Quick Start

```bash
# Before coding a complex feature
cat .agent/CONTEXT_INDEX.md
cat .agent/FEATURES/{relevant-area}.md

# Using AI CLI with context (auto-detects Claude Code or Codex)
./scripts/ai-with-context.sh "your task description"
```

**Important:** Even if you're coding manually (not using AI), follow patterns documented in `.agent/CODING_STANDARDS.md` to maintain consistency.

## License

MIT
