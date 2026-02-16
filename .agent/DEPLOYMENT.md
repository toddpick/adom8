# Deployment

## Build Process

```bash
# 1. Build solution
dotnet build src/AIAgents.sln

# 2. Run tests
dotnet test src/AIAgents.sln

# 3. Publish
dotnet publish src/AIAgents.Functions/AIAgents.Functions.csproj -c Release -o ./publish

# 4. Create zip
Compress-Archive -Path ./publish/* -DestinationPath ./publish.zip -Force

# 5. Deploy
az functionapp deployment source config-zip \
    --name ai-agents-func-todd \
    --resource-group ai-agents-rg \
    --src ./publish.zip
```

## Infrastructure (Terraform)

### Files
| File | Resources |
|------|-----------|
| `main.tf` | Resource group, provider config |
| `functions.tf` | Service plan (Consumption Y1), Function App, app settings |
| `storage.tf` | Storage account, queues (`agent-tasks`, `agent-tasks-poison`), table (`activitylog`), container (`temp-repos`) |
| `static-web-app.tf` | Static Web App (Free tier) |
| `monitoring.tf` | App Insights, action group, metric alerts (5xx, queue depth, duration) |
| `variables.tf` | Input variables |
| `outputs.tf` | URLs, connection strings, next steps |

### Deploying Infrastructure
```bash
cd infrastructure
terraform init
terraform plan -var-file="terraform.tfvars"
terraform apply -var-file="terraform.tfvars"
```

### Required Variables
```hcl
resource_group_name  = "ai-agents-rg"
location            = "eastus"
environment         = "dev"
function_app_name   = "ai-agents-func-todd"
storage_account_name = "aiagentsstortodd"
static_web_app_name = "ai-agents-dash-todd"
alert_email         = "you@example.com"
```

## Function App Settings

Settings use `__` separator for nested config:

| Setting | Section | Purpose |
|---------|---------|---------|
| `AI__Provider` | AI | Claude, OpenAI, AzureOpenAI, Google, OpenRouter |
| `AI__Model` | AI | Model name (e.g., claude-sonnet-4-20250514) |
| `AI__ApiKey` | AI | Provider API key |
| `AI__MaxTokens` | AI | Default max tokens (4096) |
| `AI__Temperature` | AI | Default temperature (0.3) |
| `AzureDevOps__OrganizationUrl` | ADO | Organization URL |
| `AzureDevOps__Pat` | ADO | Personal access token |
| `AzureDevOps__Project` | ADO | Project name |
| `Git__Provider` | Git | "GitHub" or "AzureDevOps" |
| `Git__RepositoryUrl` | Git | Full clone URL |
| `Git__Token` | Git | Auth token for push |
| `Git__Email` | Git | Commit author email |
| `Git__Name` | Git | Commit author name |
| `GitHub__Owner` | GitHub | Repo owner (for PR creation) |
| `GitHub__Repo` | GitHub | Repo name (for PR creation) |
| `GitHub__Token` | GitHub | GitHub PAT |
| `Deployment__DefaultAutonomyLevel` | Deployment | 1-5 (default: 3) |
| `Deployment__DefaultMinimumReviewScore` | Deployment | 0-100 (default: 85) |
| `WEBSITE_RUN_FROM_PACKAGE` | System | Must be `1` for zip deploy |
| `FUNCTIONS_WORKER_RUNTIME` | System | Must be `dotnet-isolated` |
| `FUNCTIONS_EXTENSION_VERSION` | System | Must be `~4` |

## Dashboard Deployment

Dashboard is a static site deployed to Azure Static Web Apps:

```bash
cd dashboard
npx @azure/static-web-apps-cli deploy . \
    --deployment-token "<token>" \
    --env production
```

## ADO Service Hook Setup

After deployment, configure an ADO Service Hook:
1. Project Settings → Service Hooks → Create Subscription
2. Event: "Work item updated"
3. Filter: State changes on User Story type
4. Action: Web Hook POST to `https://<func-app>.azurewebsites.net/api/webhook?code=<function-key>`

## Key Endpoints

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `/api/webhook` | POST | Function key | ADO service hook receiver |
| `/api/health` | GET | Anonymous | Health check |
| `/api/status` | GET | Anonymous | Dashboard data |
| `/api/emergency-stop` | GET/POST | Anonymous | Queue monitoring / clear |
| `/api/analyze-codebase` | POST | Function key | Trigger codebase scan |
| `/api/codebase-intelligence` | GET | Function key | Get scan metadata |
