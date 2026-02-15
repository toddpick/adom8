# Setup Guide — AI Development Agents for Azure DevOps

> **End-to-end walkthrough:** from a blank Azure subscription + ADO org to a fully working AI agent pipeline.
> Estimated time: **45–60 minutes** for first-time setup.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Create Personal Access Tokens](#2-create-personal-access-tokens)
   - [2a. Azure DevOps PAT](#2a-azure-devops-pat)
   - [2b. GitHub PAT](#2b-github-pat-if-using-github-for-code)
3. [Customize Your ADO Board (States & Fields)](#3-customize-your-ado-board-states--fields)
4. [Clone the Repository](#4-clone-the-repository)
5. [Deploy Azure Infrastructure (Terraform)](#5-deploy-azure-infrastructure-terraform)
   - [5d. VS Enterprise / MSDN Subscription Notes](#5d-visual-studio-enterprise--msdn-subscription-notes)
6. [Configure the Azure Function App](#6-configure-the-azure-function-app)
7. [Deploy the Functions Code](#7-deploy-the-functions-code)
8. [Deploy the Dashboard](#8-deploy-the-dashboard)
9. [Configure the ADO Service Hook (Webhook)](#9-configure-the-ado-service-hook-webhook)
10. [End-to-End Test](#10-end-to-end-test)
11. [CI/CD: Automated Deployments](#11-cicd-automated-deployments)
12. [Optional: Terraform via Azure DevOps Pipeline](#12-optional-terraform-via-azure-devops-pipeline)
13. [Local Development](#13-local-development)
14. [Troubleshooting](#14-troubleshooting)

---

## 1. Prerequisites

Install these before you begin:

| Tool | Version | Install Link |
|------|---------|-------------|
| **Azure CLI** | latest | [install](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) |
| **.NET 8 SDK** | 8.0+ | [install](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Azure Functions Core Tools** | v4 | [install](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local) |
| **Terraform** | ≥ 1.0 | [install](https://www.terraform.io/downloads) |
| **Git** | latest | [install](https://git-scm.com/downloads) |

**Accounts needed:**
- Azure subscription with `Contributor` role (to create resources)
- Azure DevOps organization + project
- AI API key — one of: [Anthropic (Claude)](https://console.anthropic.com/), [OpenAI](https://platform.openai.com/), or [Azure OpenAI](https://learn.microsoft.com/en-us/azure/ai-services/openai/)

---

## 2. Create Personal Access Tokens

### 2a. Azure DevOps PAT

The agents need a Personal Access Token to read/write work items, push code, create PRs, and (optionally) trigger pipelines.

1. Open Azure DevOps → click your **profile icon** (top-right) → **Personal access tokens**
2. Click **+ New Token**
3. Configure:
   - **Name:** `AI Agent Bot`
   - **Organization:** your org
   - **Expiration:** 90 days (or custom)
   - **Scopes → Custom defined** — select these:

| Scope | Permission |
|-------|-----------|
| **Work Items** | Read & Write |
| **Code** | Read & Write |
| **Pull Request Threads** | Read & Write |
| **Build** | Read & Execute *(Level 5 autonomy only)* |
| **Pipeline Resources** | Use and manage *(Level 5 autonomy only)* |

4. Click **Create** → **copy the token immediately** (you won't see it again)

> **Tip:** If using Azure DevOps Repos (not GitHub) for your code, this same PAT is used for `AzureDevOps__Pat`, `Git__Token`, and code operations.

### 2b. GitHub PAT (if using GitHub for code)

If your target code repository is on GitHub (recommended for POC), you need a GitHub Personal Access Token.

1. Go to [github.com](https://github.com) → click your **profile icon** → **Settings**
2. Click **Developer settings** → **Personal access tokens**
3. Choose either **Classic** or **Fine-grained** token:

**Option 1: Classic Token** (simpler)

Click **Tokens (classic)** → **Generate new token (classic)**:

| Scope | Why |
|-------|-----|
| **repo** (full control) | Push branches, create PRs |
| **workflow** | Trigger deploy workflows (Level 5 autonomy) |

**Option 2: Fine-grained Token** (more secure — scoped to one repo)

Click **Fine-grained tokens** → **Generate new token**:
- **Repository access:** Select your target repo only
- **Permissions:**

| Permission | Access | Why |
|------------|--------|-----|
| **Contents** | Read and Write | Push code to feature branches |
| **Pull requests** | Read and Write | Create PRs for code review |
| **Actions** | Read and Write | Trigger workflow_dispatch (Level 5 autonomy) |
| **Metadata** | Read | Auto-granted, required |

4. Click **Generate token** → **copy immediately**

> **Tip:** This GitHub PAT is used for three settings: `Git__Token`, `GitHub__Token`, and `GitHub__Owner`/`GitHub__Repo` configuration. It's the same token for all three.

---

## 3. Customize Your ADO Board (States & Fields)

This is the most critical step. The agent pipeline transitions User Stories through custom states that **don't exist by default** in Azure DevOps. You must add them.

### 3a. Create an Inherited Process

Azure DevOps doesn't let you modify built-in processes directly. You need to create an inherited copy:

1. Go to **Organization Settings** (gear icon, bottom-left of ADO)
2. Click **Boards → Process**
3. Find your current process (usually **Agile**, **Scrum**, or **CMMI**)
4. Click the **⋯** menu → **Create inherited process**
5. Name it: `Agile - AI Agents` (or similar)
6. Click **Create process**

### 3b. Switch Your Project to the Inherited Process

1. Still in **Organization Settings → Process**
2. Click on your new `Agile - AI Agents` process
3. Click the **Projects** tab
4. Click **Change team projects to use Agile - AI Agents**
5. Select your project → **OK**

### 3c. Add Custom States to User Story

1. In your inherited process, click **User Story** under "Work item types"
2. Click the **States** tab
3. You'll see the default states (New, Active, Resolved, Closed, Removed)
4. Click **+ New state** for each of the following — **use the exact names and categories shown:**

| State Name | State Category | Color (suggested) | Purpose |
|------------|---------------|-------------------|---------|
| `Story Planning` | In Progress | 🔵 Blue | AI Planning Agent is generating the implementation plan |
| `AI Code` | In Progress | 🟣 Purple | AI Coding Agent is generating source code |
| `AI Test` | In Progress | 🟠 Orange | AI Testing Agent is generating unit/integration tests |
| `AI Review` | In Progress | 🟡 Yellow | AI Review Agent is performing code review |
| `AI Docs` | In Progress | 🔵 Dark Blue | AI Documentation Agent is generating docs + PR |
| `AI Deployment` | In Progress | ⚫ Gray | AI Deployment Agent is evaluating merge/deploy |
| `Code Review` | In Progress | 🟢 Green | Waiting for human code review (Autonomy Level 3) |
| `Needs Revision` | In Progress | 🔴 Red | Review score too low — human intervention required |
| `Agent Failed` | In Progress | 🔴 Dark Red | Agent exhausted retries or hit a permanent error |
| `Ready for QA` | Resolved | 🟢 Green | All AI agents done — ready for QA testing |
| `Ready for Deployment` | Resolved | 🟢 Green | PR merged — ready for deployment |
| `Deployed` | Completed | ✅ Green | Merged + deployed (Autonomy Level 5) |

> **Important:** The **State Name** must match exactly (case-sensitive). The **State Category** controls how Azure DevOps sorts cards on the board and calculates cycle time.

5. After adding all states, click **Save**

### 3d. Arrange the Board Columns (Optional but Recommended)

1. Go to your project's **Boards → Boards**
2. Click ⚙️ **Configure team settings** (gear icon top-right of the board)
3. Click the **Columns** tab
4. Add columns matching the pipeline flow. Suggested layout:

```
New → Story Planning → AI Code → AI Test → AI Review → AI Docs → AI Deployment → Code Review → Needs Revision → Agent Failed → Ready for QA → Ready for Deployment → Deployed
```

Map each column to its matching state. This gives you a visual Kanban board showing stories flowing through the AI pipeline.

> **Note:** You don't need to add a "Closed" column — Azure DevOps automatically includes a rightmost column for the **Completed** category (which contains "Deployed" and the default "Closed" state). Stories in "Deployed" will appear in that final column. The default "Closed" state can be used to archive stories that are fully done.

### 3e. Add Custom Fields

Still in **Organization Settings → Process → User Story**:

The AI pipeline uses **20 custom fields** organized into three groups: **Input Fields** (you set per-story), **Model Override Fields** (optional per-story AI model control), and **AI Tracking** (written automatically by agents).

#### Input Fields — "AI Agent Settings" Group

1. Click the **Layout** tab
2. Click **+ New field** to add the first field:
   - **Name:** `AI Autonomy Level`
   - **Type:** Integer
   - **Default:** `3`
   - **Description:** Controls how far the AI pipeline goes automatically (1-5)
   - **Group:** Select **"Create new group"** → name it **AI Agent Settings**
   - **Page:** Details

3. Click **+ New field** again:
   - **Name:** `AI Minimum Review Score`
   - **Type:** Integer
   - **Default:** `85`
   - **Description:** Min score (0–100) for auto-merge at Levels 4–5
   - **Group:** Select **AI Agent Settings**
   - **Page:** Details

#### Per-Story Model Override Fields — "AI Model Settings" Group

These fields let users override which AI model each agent uses on a per-story basis. Leave them blank to use the system defaults. Most users won't touch these — they exist for stories that need heavier (or lighter) models than the default.

> **Important:** All fields in this group use **Picklist (string)** type, not plain String. This prevents typos and ensures users pick valid model identifiers.

4. Click **+ New field**:
   - **Name:** `AI Model Tier`
   - **Type:** Picklist (string)
   - **Items:** `Standard`, `Premium`, `Economy`
   - **Description:** Quick preset that switches all agents to a pre-configured model set
   - **Group:** Select **"Create new group"** → name it **AI Model Settings**
   - **Page:** Details

5. Click **+ New field**:
   - **Name:** `AI Planning Model`
   - **Type:** Picklist (string)
   - **Items:** *(use the model picklist below)*
   - **Description:** Override model for the Planning agent
   - **Group:** Select **AI Model Settings**
   - **Page:** Details

6. Click **+ New field**:
   - **Name:** `AI Coding Model`
   - **Type:** Picklist (string)
   - **Items:** *(use the model picklist below)*
   - **Description:** Override model for the Coding agent
   - **Group:** Select **AI Model Settings**
   - **Page:** Details

7. Click **+ New field**:
   - **Name:** `AI Testing Model`
   - **Type:** Picklist (string)
   - **Items:** *(use the model picklist below)*
   - **Description:** Override model for the Testing agent
   - **Group:** Select **AI Model Settings**
   - **Page:** Details

8. Click **+ New field**:
   - **Name:** `AI Review Model`
   - **Type:** Picklist (string)
   - **Items:** *(use the model picklist below)*
   - **Description:** Override model for the Review agent
   - **Group:** Select **AI Model Settings**
   - **Page:** Details

9. Click **+ New field**:
   - **Name:** `AI Documentation Model`
   - **Type:** Picklist (string)
   - **Items:** *(use the model picklist below)*
   - **Description:** Override model for the Documentation agent
   - **Group:** Select **AI Model Settings**
   - **Page:** Details

**Model Picklist Values** — Add these items to each of the 5 per-agent model fields:

| Picklist Value | Provider | Tier | Best For |
|---|---|---|---|
| `claude-opus-4-20250514` | Claude | Premium | Complex architecture, difficult refactors |
| `claude-sonnet-4.5-20250514` | Claude | Premium | Latest Sonnet, strong reasoning + code |
| `claude-sonnet-4-20250514` | Claude | Standard | Great all-rounder, default recommendation |
| `claude-haiku-4.5` | Claude | Economy | Fast & cheap, formulaic tasks |
| `gpt-5.1-codex` | OpenAI | Premium | Code-specialized, large context |
| `gpt-5.1-codex-mini` | OpenAI | Economy | Code-specialized, budget-friendly |
| `gpt-4.1` | OpenAI | Standard | Strong reasoning + code generation |
| `gpt-4o` | OpenAI | Standard | Fast, versatile, good code review |
| `gpt-5-mini` | OpenAI | Economy | Cheap, handles structured tasks well |
| `gemini-2.5-pro` | Google | Standard | Long context, good analysis |
| `gemini-3-flash` | Google | Economy | Fastest, cheapest, great for docs |

> **Tip:** You don't need to add every model — just the ones relevant to the providers you have API keys for. You can always add more later in **Organization Settings → Process → Fields → AI Planning Model → Edit picklist**.

#### Output Fields — "AI Tracking" Group

These fields are written automatically by the agents during processing. They show up on every user story so you can see exactly what the AI did.

10. Click **+ New field**:
   - **Name:** `AI Tokens Used`
   - **Type:** Integer
   - **Description:** Total tokens consumed across all agents
   - **Group:** Select **"Create new group"** → name it **AI Tracking**
   - **Page:** Details

11. Click **+ New field**:
   - **Name:** `AI Cost`
   - **Type:** String
   - **Description:** Estimated USD cost (e.g., "$0.1234")
   - **Group:** Select **AI Tracking**
   - **Page:** Details

12. Click **+ New field**:
   - **Name:** `AI Complexity`
   - **Type:** String
   - **Description:** Complexity classification: XS, S, M, L, XL
   - **Group:** Select **AI Tracking**
   - **Page:** Details

13. Click **+ New field**:
   - **Name:** `AI Model`
   - **Type:** String
   - **Description:** Per-agent AI model breakdown (e.g., "Planning: gpt-4o, Coding: claude-opus")
   - **Group:** Select **AI Tracking**
   - **Page:** Details

14. Click **+ New field**:
   - **Name:** `AI Review Score`
   - **Type:** Integer
   - **Description:** Code review score (0–100) from the Review agent
   - **Group:** Select **AI Tracking**
   - **Page:** Details

15. Click **+ New field**:
   - **Name:** `AI Processing Time`
   - **Type:** Decimal
   - **Description:** Total pipeline processing time in seconds
   - **Group:** Select **AI Tracking**
   - **Page:** Details

16. Click **+ New field**:
    - **Name:** `AI Files Generated`
    - **Type:** Integer
    - **Description:** Number of source code files generated
    - **Group:** Select **AI Tracking**
    - **Page:** Details

17. Click **+ New field**:
    - **Name:** `AI Tests Generated`
    - **Type:** Integer
    - **Description:** Number of test cases generated
    - **Group:** Select **AI Tracking**
    - **Page:** Details

18. Click **+ New field**:
    - **Name:** `AI PR Number`
    - **Type:** Integer
    - **Description:** Pull request number created for this story
    - **Group:** Select **AI Tracking**
    - **Page:** Details

19. Click **+ New field**:
    - **Name:** `AI Last Agent`
    - **Type:** String
    - **Description:** Last agent that processed this story
    - **Group:** Select **AI Tracking**
    - **Page:** Details

20. Click **+ New field**:
    - **Name:** `AI Critical Issues`
    - **Type:** Integer
    - **Description:** Critical issues found during code review
    - **Group:** Select **AI Tracking**
    - **Page:** Details

21. Click **+ New field**:
    - **Name:** `AI Deployment Decision`
    - **Type:** String
    - **Description:** Final deployment action taken (e.g., "Auto-merged", "Assigned for human review")
    - **Group:** Select **AI Tracking**
    - **Page:** Details

> **Note:** ADO auto-generates the reference names from the field names (e.g., `AI Tokens Used` → `Custom.AITokensUsed`). You won't see a separate input for this — it happens automatically.

**What you'll see on each user story after agents process it:**

| Group | Field | Example Value |
|-------|-------|---------------|
| AI Agent Settings | AI Autonomy Level | 3 |
| AI Agent Settings | AI Minimum Review Score | 85 |
| AI Model Settings | AI Model Tier | *(blank = Standard)* |
| AI Model Settings | AI Planning Model | *(blank = use default)* |
| AI Model Settings | AI Coding Model | *(blank = use default)* |
| AI Model Settings | AI Testing Model | *(blank = use default)* |
| AI Model Settings | AI Review Model | *(blank = use default)* |
| AI Model Settings | AI Documentation Model | *(blank = use default)* |
| AI Tracking | AI Tokens Used | 24,500 |
| AI Tracking | AI Cost | $0.1234 |
| AI Tracking | AI Complexity | M |
| AI Tracking | AI Model | Coding: gpt-4o, Planning: gpt-4o, Review: gpt-4o-mini |
| AI Tracking | AI Review Score | 92 |
| AI Tracking | AI Processing Time | 45.3 |
| AI Tracking | AI Files Generated | 5 |
| AI Tracking | AI Tests Generated | 12 |
| AI Tracking | AI PR Number | 42 |
| AI Tracking | AI Last Agent | Deployment |
| AI Tracking | AI Critical Issues | 0 |
| AI Tracking | AI Deployment Decision | Auto-merged PR #42 |

**Model override resolution order** (most specific wins):

| Priority | Source | Example |
|----------|--------|---------|
| 1 (highest) | Per-agent field on story | `AI Coding Model = "claude-opus-4-20250514"` |
| 2 | Model Tier on story | `AI Model Tier = "Premium"` → looks up tier config |
| 3 | Config per-agent default | `AI__AgentModels__Coding__Model = "codex-mini-latest"` |
| 4 (lowest) | Global default | `AI__Model = "claude-sonnet-4-20250514"` |

> **Tip:** 99% of the time, just leave the Model Settings fields blank and the system defaults are used. Set `AI Model Tier = "Premium"` when you have a complex story that needs heavier models. Use the per-agent fields when you want surgical control (e.g., trying Opus on just the Coding agent for one story).

**Autonomy Level reference:**

| Level | Name | Pipeline Stops After |
|-------|------|---------------------|
| 1 | Plan Only | Planning agent — generates plan, no code |
| 2 | Code Only | Testing agent — generates code + tests, no review/merge |
| 3 | Review & Pause | All agents run → pauses at "Code Review" for human approval |
| 4 | Auto-Merge | All agents run → auto-merges PR if review score meets threshold |
| 5 | Full Autonomy | All agents run → auto-merges + triggers deployment pipeline |

> **Tip:** If you skip the AI Tracking fields, the pipeline still works — it just won't display the values on the work item. The agents gracefully handle missing fields. But you MUST add the two AI Agent Settings fields (Autonomy Level and Minimum Review Score) for the pipeline to function correctly. The AI Model Settings fields are entirely optional — skip them if you don't need per-story model control.

---

## 4. Clone the Repository

```bash
git clone https://github.com/toddpick/ADO-Agent.git
cd ADO-Agent
```

---

## 5. Deploy Azure Infrastructure (Terraform)

Terraform creates: Resource Group, Storage Account (queues + state), Function App (Consumption plan), Static Web App (dashboard), and Application Insights.

### 5a. Authenticate to Azure

```bash
az login
az account set --subscription "<YOUR_SUBSCRIPTION_ID>"
```

### 5b. Configure Terraform Variables

```bash
cd infrastructure

# Create your variables file from the example
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars`:

```hcl
resource_group_name  = "ai-agents-rg"
location             = "eastus"            # Choose your preferred region
environment          = "dev"
function_app_name    = "ai-agents-func-YOURNAME"    # Must be globally unique
storage_account_name = "aiagentsstoryourname"        # Globally unique, lowercase, no hyphens, 3-24 chars
static_web_app_name  = "ai-agent-dashboard-YOURNAME" # Globally unique
alert_email          = "your-email@example.com"      # Receives monitoring alert notifications
```

### 5c. Run Terraform

```bash
terraform init        # Download providers
terraform plan        # Preview what will be created — review carefully
terraform apply       # Type 'yes' to confirm
```

On success you'll see outputs like:

```
function_app_name         = "ai-agents-func-YOURNAME"
function_app_url          = "https://ai-agents-func-YOURNAME.azurewebsites.net"
orchestrator_webhook_url  = "https://ai-agents-func-YOURNAME.azurewebsites.net/api/OrchestratorWebhook"
dashboard_url             = "https://your-dashboard.azurestaticapps.net"
```

**Save these values** — you'll need them in the next steps.

> To see outputs later: `terraform output`  
> For sensitive values: `terraform output -raw storage_connection_string`

### 5d. Visual Studio Enterprise / MSDN Subscription Notes

If you're deploying under a **Visual Studio Enterprise** (or other MSDN) subscription, you may encounter a **"Dynamic VMs quota = 0"** error when Terraform tries to create the Consumption plan. This is a known limitation — the Terraform API creates the plan differently than the Azure CLI.

**Workaround:** Create the Function App via Azure CLI first, then import into Terraform:

```bash
# 1. Create the function app (Azure auto-creates the consumption plan named "<Region>Plan")
az functionapp create \
  --name <YOUR_FUNCTION_APP_NAME> \
  --resource-group ai-agents-rg \
  --storage-account <YOUR_STORAGE_ACCOUNT> \
  --consumption-plan-location eastus \
  --runtime dotnet-isolated \
  --runtime-version 8 \
  --os-type Windows \
  --app-insights <YOUR_FUNCTION_APP_NAME>-insights \
  --functions-version 4

# 2. Import both resources into Terraform state
terraform import azurerm_service_plan.functions \
  "/subscriptions/<SUB_ID>/resourceGroups/ai-agents-rg/providers/Microsoft.Web/serverFarms/EastUSPlan"

terraform import azurerm_windows_function_app.agents \
  "/subscriptions/<SUB_ID>/resourceGroups/ai-agents-rg/providers/Microsoft.Web/sites/<YOUR_FUNCTION_APP_NAME>"

# 3. Apply remaining resources (alerts, etc.)
terraform apply
```

> **Cost note:** This entire POC runs on free-tier or near-free resources. A typical Visual Studio Enterprise subscription gets $150/month in Azure credits — this POC will use **< $5/month** even with regular usage. The Consumption (Y1) plan gives you 1 million free executions/month.

---

## 6. Configure the Azure Function App

Set the application settings that the Functions code reads at runtime. Replace every `<placeholder>` with your actual values.

### Option A: GitHub as code repository (recommended for POC)

```bash
az functionapp config appsettings set \
  --name <YOUR_FUNCTION_APP_NAME> \
  --resource-group ai-agents-rg \
  --settings \
    "AI__Provider=Claude" \
    "AI__Model=claude-sonnet-4-20250514" \
    "AI__ApiKey=<YOUR_AI_API_KEY>" \
    "AI__MaxTokens=4096" \
    "AI__Temperature=0.3" \
    "AzureDevOps__OrganizationUrl=https://dev.azure.com/<YOUR_ORG>" \
    "AzureDevOps__Pat=<YOUR_ADO_PAT>" \
    "AzureDevOps__Project=<YOUR_PROJECT>" \
    "Git__Provider=GitHub" \
    "Git__RepositoryUrl=https://github.com/<OWNER>/<REPO>.git" \
    "Git__Username=x-token-auth" \
    "Git__Token=<YOUR_GITHUB_PAT>" \
    "Git__Email=ai-agent@your-org.com" \
    "Git__Name=AI Agent Bot" \
    "GitHub__Owner=<GITHUB_OWNER_OR_ORG>" \
    "GitHub__Repo=<GITHUB_REPO_NAME>" \
    "GitHub__Token=<YOUR_GITHUB_PAT>" \
    "GitHub__DeployWorkflow=deploy.yml" \
    "Deployment__PipelineName=Deploy-To-Production" \
    "Deployment__DefaultAutonomyLevel=3" \
    "Deployment__DefaultMinimumReviewScore=85"
```

### Option B: Azure DevOps Repos as code repository

```bash
az functionapp config appsettings set \
  --name <YOUR_FUNCTION_APP_NAME> \
  --resource-group ai-agents-rg \
  --settings \
    "AI__Provider=Claude" \
    "AI__Model=claude-sonnet-4-20250514" \
    "AI__ApiKey=<YOUR_AI_API_KEY>" \
    "AI__MaxTokens=4096" \
    "AI__Temperature=0.3" \
    "AzureDevOps__OrganizationUrl=https://dev.azure.com/<YOUR_ORG>" \
    "AzureDevOps__Pat=<YOUR_ADO_PAT>" \
    "AzureDevOps__Project=<YOUR_PROJECT>" \
    "Git__Provider=AzureDevOps" \
    "Git__RepositoryUrl=https://dev.azure.com/<YOUR_ORG>/<YOUR_PROJECT>/_git/<YOUR_REPO>" \
    "Git__Username=x-token-auth" \
    "Git__Token=<YOUR_ADO_PAT>" \
    "Git__Email=ai-agent@your-org.com" \
    "Git__Name=AI Agent Bot" \
    "Deployment__PipelineName=Deploy-To-Production" \
    "Deployment__PipelineId=" \
    "Deployment__DefaultAutonomyLevel=3" \
    "Deployment__DefaultMinimumReviewScore=85"
```

**AI Provider options:**

| Provider | AI__Provider | AI__Endpoint | AI__Model |
|----------|-------------|-------------|-----------|
| Anthropic (Claude) | `Claude` | *(leave empty)* | `claude-sonnet-4-20250514` |
| OpenAI | `OpenAI` | *(leave empty)* | `gpt-4o` |
| Azure OpenAI | `AzureOpenAI` | `https://<resource>.openai.azure.com/` | your deployment name |

**Git Provider options:**

| Setting | GitHub | Azure DevOps Repos |
|---------|--------|--------------------|
| `Git__Provider` | `GitHub` | `AzureDevOps` |
| `Git__RepositoryUrl` | `https://github.com/owner/repo.git` | `https://dev.azure.com/org/project/_git/repo` |
| `Git__Token` | GitHub PAT with `repo` scope | ADO PAT with `Code: Read & Write` |
| `GitHub__*` settings | **Required** (Owner, Repo, Token) | Not needed |
| Level 5 deploy trigger | `GitHub__DeployWorkflow` (e.g., `deploy.yml`) | `Deployment__PipelineId` (numeric) |

### 6c. (Optional) Per-Agent Model Defaults

By default, all agents use the global `AI__Model`. To assign different models to specific agents at the config level:

```bash
az functionapp config appsettings set \
  --name <YOUR_FUNCTION_APP_NAME> \
  --resource-group ai-agents-rg \
  --settings \
    "AI__AgentModels__Planning__Model=claude-sonnet-4-20250514" \
    "AI__AgentModels__Coding__Model=claude-sonnet-4-20250514" \
    "AI__AgentModels__Testing__Model=gpt-4o-mini" \
    "AI__AgentModels__Review__Model=gpt-4o" \
    "AI__AgentModels__Documentation__Model=gemini-2.0-flash"
```

If an agent needs a different provider/endpoint/key (e.g., Testing uses OpenAI while default is Claude):

```bash
az functionapp config appsettings set \
  --name <YOUR_FUNCTION_APP_NAME> \
  --resource-group ai-agents-rg \
  --settings \
    "AI__AgentModels__Testing__Provider=OpenAI" \
    "AI__AgentModels__Testing__Model=gpt-4o-mini" \
    "AI__AgentModels__Testing__ApiKey=sk-..." \
    "AI__AgentModels__Documentation__Provider=OpenAI" \
    "AI__AgentModels__Documentation__Model=gemini-2.0-flash" \
    "AI__AgentModels__Documentation__Endpoint=https://generativelanguage.googleapis.com/v1beta/openai" \
    "AI__AgentModels__Documentation__ApiKey=AIza..."
```

### 6d. (Optional) Model Tier Presets

Tiers let users pick "Premium" or "Economy" on a user story to switch all agents at once. Configure the tier→model mappings:

```bash
az functionapp config appsettings set \
  --name <YOUR_FUNCTION_APP_NAME> \
  --resource-group ai-agents-rg \
  --settings \
    "AI__ModelTiers__Premium__Planning__Model=claude-opus-4-20250514" \
    "AI__ModelTiers__Premium__Coding__Model=claude-opus-4-20250514" \
    "AI__ModelTiers__Premium__Review__Model=claude-sonnet-4-20250514" \
    "AI__ModelTiers__Economy__Testing__Model=gpt-4o-mini" \
    "AI__ModelTiers__Economy__Documentation__Model=gemini-2.0-flash" \
    "AI__ModelTiers__Economy__Planning__Model=gpt-4o-mini" \
    "AI__ModelTiers__Economy__Coding__Model=gpt-4o-mini" \
    "AI__ModelTiers__Economy__Review__Model=gpt-4o-mini"
```

> **How it works:** When a user sets `AI Model Tier = "Premium"` on a story, every agent checks the `AI:ModelTiers:Premium:{agentName}` config. Agents not defined in the tier fall back to the config-level per-agent defaults, then to the global default. Per-agent fields on the story (e.g., `AI Coding Model = "codex-mini-latest"`) always take highest priority.

> **Note:** For Level 5 autonomy, GitHub dispatches a `workflow_dispatch` event to the configured GitHub Actions workflow. Azure DevOps triggers an ADO pipeline by ID.

---

## 7. Deploy the Functions Code

```bash
cd src/AIAgents.Functions

# Build
dotnet publish -c Release --output ./publish

# Deploy to Azure
func azure functionapp publish <YOUR_FUNCTION_APP_NAME>
```

Verify it deployed:

```bash
# Should return a list of your functions
func azure functionapp list-functions <YOUR_FUNCTION_APP_NAME>
```

You should see: `OrchestratorWebhook`, `AgentTaskDispatcher`, `GetCurrentStatus`.

---

## 8. Deploy the Dashboard

### Option A: Azure CLI (quick)

```bash
# Get the deployment token
cd infrastructure
DEPLOY_TOKEN=$(terraform output -raw dashboard_api_key)

# Deploy
cd ../dashboard
npx @azure/static-web-apps-cli deploy \
  --deployment-token $DEPLOY_TOKEN \
  --app-location "."
```

### Option B: GitHub Actions (automated — push to deploy)

1. Get your deployment token:
   ```bash
   terraform output -raw dashboard_api_key
   ```
2. In your GitHub repo → **Settings → Secrets and variables → Actions**
3. Add secret: `AZURE_STATIC_WEB_APPS_API_TOKEN` = the token
4. Any push to `main` that changes `dashboard/**` will auto-deploy

### Update Dashboard API URL

Open `dashboard/index.html` and set the API URL to your Function App:

```javascript
const API_URL = 'https://<YOUR_FUNCTION_APP>.azurewebsites.net/api';
```

Re-deploy the dashboard after this change.

---

## 9. Configure the ADO Service Hook (Webhook)

This connects Azure DevOps to your Function App. When a work item's state changes, ADO sends a webhook that triggers the agent pipeline.

1. Go to your ADO project → **Project Settings** (gear, bottom-left)
2. Under **General**, click **Service hooks**
3. Click **+ Create subscription**
4. Choose **Web Hooks** → **Next**
5. Configure the trigger:
   - **Trigger on this type of event:** `Work item updated`
   - **Area path:** *(leave as `[Any]` or scope to specific area)*
   - **Work item type:** `User Story`
   - **Field:** `State`
6. Click **Next**
7. Configure the action:
   - **URL:** `https://<YOUR_FUNCTION_APP>.azurewebsites.net/api/OrchestratorWebhook`
   - **HTTP headers:** *(leave empty)*
   - **Resource details to send:** `All`
   - **Messages to send:** `All`
8. Click **Test** → you should see a `200 OK` response with `{"status":"skipped",...}` (expected — the test payload isn't a real state change)
9. Click **Finish**

> **Security:** The webhook URL uses Function-level auth by default. To add extra security, append a function key: `https://...azurewebsites.net/api/OrchestratorWebhook?code=<FUNCTION_KEY>`  
> Get it from: Azure Portal → Function App → Functions → OrchestratorWebhook → Function Keys

---

## 10. End-to-End Test

1. Go to your ADO **Boards** → **Work Items**
2. Create a new **User Story**:
   - **Title:** `Create a hello world REST API endpoint`
   - **Description:** `Build a simple GET /hello endpoint that returns { "message": "Hello, World!" } with proper error handling and logging.`
   - **Acceptance Criteria:** `Given a GET request to /hello, When the server is running, Then it returns 200 with the hello message`
   - *(Optional)* **AI Autonomy Level:** `3` (default)
   - *(Optional)* **AI Minimum Review Score:** `85` (default)
3. Change the state to **`Story Planning`**
4. **Watch the pipeline work:**

| What to check | Where |
|---------------|-------|
| Agent pipeline running | Dashboard URL from Step 8 |
| Queue messages | Azure Portal → Storage Account → Queues → `agent-tasks` |
| Function logs | Azure Portal → Function App → Monitor, or `az functionapp log stream --name <name> --resource-group ai-agents-rg` |
| Detailed telemetry | Azure Portal → Application Insights → Transaction search |
| Generated artifacts | Your repo → branch `feature/US-<id>` → `.ado/stories/US-<id>/` |
| Work item state progression | ADO Boards — watch the card move through columns |

**Expected flow:**
```
Story Planning → AI Code → AI Test → AI Review → AI Docs → AI Deployment → Code Review
                                                                            (Level 3 stops here)
```

5. You should see a **PR created** in your repository and a **comment on the work item** summarizing what each agent did.

---

## 11. CI/CD: Automated Deployments

The repo includes GitHub Actions workflows for continuous deployment:

### Functions (`.github/workflows/deploy-functions.yml`)

Triggers on push to `main` when `src/**` changes.

**Required GitHub Secrets:**

| Secret | Value | How to get it |
|--------|-------|--------------|
| `AZURE_FUNCTIONAPP_NAME` | Your function app name | `terraform output -raw function_app_name` |
| `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` | XML publish profile | Azure Portal → Function App → **Get publish profile** (download button) |

### Dashboard (`.github/workflows/deploy-dashboard.yml`)

Triggers on push to `main` when `dashboard/**` changes.

**Required GitHub Secrets:**

| Secret | Value | How to get it |
|--------|-------|--------------|
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Deployment token | `terraform output -raw dashboard_api_key` |

---

## 12. Optional: Terraform via Azure DevOps Pipeline

If you prefer managing infrastructure through Azure DevOps pipelines rather than running Terraform locally:

### 12a. Store Terraform State Remotely

Add a backend block to `infrastructure/main.tf`:

```hcl
terraform {
  backend "azurerm" {
    resource_group_name  = "terraform-state-rg"
    storage_account_name = "yourtfstateaccount"
    container_name       = "tfstate"
    key                  = "ai-agents.terraform.tfstate"
  }
}
```

Create the state storage (one-time):

```bash
az group create --name terraform-state-rg --location eastus
az storage account create --name yourtfstateaccount --resource-group terraform-state-rg --sku Standard_LRS
az storage container create --name tfstate --account-name yourtfstateaccount
```

### 12b. Create an Azure Service Connection

1. ADO → **Project Settings → Service connections**
2. **+ New service connection → Azure Resource Manager**
3. Choose **Service principal (automatic)**
4. Select your subscription → name it `Azure-AI-Agents`
5. Grant `Contributor` role on the subscription (or target resource group)

### 12c. Create the Pipeline YAML

Create `.ado/pipelines/terraform.yml` in your repo:

```yaml
trigger:
  branches:
    include: [main]
  paths:
    include: [infrastructure/*]

pool:
  vmImage: 'ubuntu-latest'

variables:
  - group: 'AI-Agents-Terraform'  # Variable group with tfvars values
  - name: serviceConnection
    value: 'Azure-AI-Agents'

stages:
  - stage: Plan
    displayName: 'Terraform Plan'
    jobs:
      - job: Plan
        steps:
          - task: TerraformInstaller@1
            inputs:
              terraformVersion: 'latest'

          - task: TerraformTaskV4@4
            displayName: 'Terraform Init'
            inputs:
              provider: 'azurerm'
              command: 'init'
              workingDirectory: '$(System.DefaultWorkingDirectory)/infrastructure'
              backendServiceArm: '$(serviceConnection)'
              backendAzureRmResourceGroupName: 'terraform-state-rg'
              backendAzureRmStorageAccountName: 'yourtfstateaccount'
              backendAzureRmContainerName: 'tfstate'
              backendAzureRmKey: 'ai-agents.terraform.tfstate'

          - task: TerraformTaskV4@4
            displayName: 'Terraform Plan'
            inputs:
              provider: 'azurerm'
              command: 'plan'
              workingDirectory: '$(System.DefaultWorkingDirectory)/infrastructure'
              environmentServiceNameAzureRM: '$(serviceConnection)'
              commandOptions: '-out=tfplan'

          - publish: '$(System.DefaultWorkingDirectory)/infrastructure/tfplan'
            artifact: 'tfplan'

  - stage: Apply
    displayName: 'Terraform Apply'
    dependsOn: Plan
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    jobs:
      - deployment: Apply
        environment: 'production'  # Requires approval gate
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self

                - task: TerraformInstaller@1
                  inputs:
                    terraformVersion: 'latest'

                - download: current
                  artifact: 'tfplan'

                - task: TerraformTaskV4@4
                  displayName: 'Terraform Init'
                  inputs:
                    provider: 'azurerm'
                    command: 'init'
                    workingDirectory: '$(System.DefaultWorkingDirectory)/infrastructure'
                    backendServiceArm: '$(serviceConnection)'
                    backendAzureRmResourceGroupName: 'terraform-state-rg'
                    backendAzureRmStorageAccountName: 'yourtfstateaccount'
                    backendAzureRmContainerName: 'tfstate'
                    backendAzureRmKey: 'ai-agents.terraform.tfstate'

                - task: TerraformTaskV4@4
                  displayName: 'Terraform Apply'
                  inputs:
                    provider: 'azurerm'
                    command: 'apply'
                    workingDirectory: '$(System.DefaultWorkingDirectory)/infrastructure'
                    environmentServiceNameAzureRM: '$(serviceConnection)'
                    commandOptions: '$(Pipeline.Workspace)/tfplan/tfplan'
```

### 12d. Create a Variable Group

1. ADO → **Pipelines → Library → + Variable group**
2. Name: `AI-Agents-Terraform`
3. Add variables matching `terraform.tfvars`:

| Variable | Value |
|----------|-------|
| `TF_VAR_function_app_name` | `ai-agents-func-YOURNAME` |
| `TF_VAR_storage_account_name` | `aiagentsstoryourname` |
| `TF_VAR_static_web_app_name` | `ai-agent-dashboard-YOURNAME` |
| `TF_VAR_resource_group_name` | `ai-agents-rg` |
| `TF_VAR_location` | `eastus` |

### 12e. Create the Pipeline

1. ADO → **Pipelines → + New pipeline**
2. Connect to your repo
3. Select **Existing Azure Pipelines YAML file**
4. Path: `.ado/pipelines/terraform.yml`
5. **Run** — first run will plan only; merge to `main` triggers Plan + Apply

### 12f. Add an Approval Gate (Recommended)

1. ADO → **Pipelines → Environments → production**
2. Click ⋯ → **Approvals and checks**
3. **+ Add check → Approvals** → add yourself (or a team)
4. Now Terraform Apply requires manual approval before executing

---

## 13. Local Development

```bash
# Install Azurite for local Storage emulation
npm install -g azurite

# Start Azurite (separate terminal)
azurite --silent --location .azurite --blobPort 10000 --queuePort 10001 --tablePort 10002

# Configure local settings
cd src/AIAgents.Functions
# Edit local.settings.json with your dev values (this file is in .gitignore)

# Run Functions locally
func start
```

To test the webhook locally, you can use a tool like [ngrok](https://ngrok.com/) to expose your local Function App:

```bash
ngrok http 7071
# Use the ngrok URL as your ADO Service Hook URL temporarily
```

---

## 14. Troubleshooting

### Service hook not firing
- Verify the webhook URL: `https://<func-app>.azurewebsites.net/api/OrchestratorWebhook`
- ADO → **Project Settings → Service hooks** → check for ❌ errors
- Test manually: `curl -X POST https://<func-app>.azurewebsites.net/api/OrchestratorWebhook -H "Content-Type: application/json" -d '{}'`

### Functions not processing queue messages
- Azure Portal → Storage Account → **Queues** → check `agent-tasks` for waiting messages
- Check `agent-tasks-poison` queue for failed messages (retried 5× then poisoned)
- Check Application Insights: **Transaction search** → filter by "AgentTaskDispatcher"

### AI API errors (429, 401, timeout)
- Verify `AI__ApiKey` is correct
- Check rate limits on your AI provider dashboard
- Increase timeout: `AI__Timeout=600` (seconds)
- Stream logs: `az functionapp log stream --name <name> --resource-group ai-agents-rg`

### Work item state not changing
- Confirm you added the exact state names from Step 3c (case-sensitive)
- Check that your PAT has `Work Items: Read & Write` scope
- Look at Function logs for errors in `OrchestratorWebhook`

### Dashboard not updating
- Verify the API URL in `dashboard/index.html` points to your Function App
- Check CORS: Azure Portal → Function App → **API → CORS** → add your dashboard URL
- Test the API directly: `curl https://<func-app>.azurewebsites.net/api/GetCurrentStatus`

### Git push failures
- Verify `Git__Token` has write permissions to the repository
- **GitHub:** PAT needs `repo` scope. URL format: `https://github.com/<owner>/<repo>.git`
- **Azure DevOps:** PAT needs `Code: Read & Write`. URL format: `https://dev.azure.com/<org>/<project>/_git/<repo>`
- Ensure the branch isn’t protected (or the PAT has bypass permissions)

### PR creation failures
- **GitHub:** Verify `GitHub__Owner`, `GitHub__Repo`, and `GitHub__Token` are set correctly
- **Azure DevOps:** Verify `Git__Provider=AzureDevOps` and `AzureDevOps__Pat` has `Code` + `Pull Request Threads` scopes
- Check Function logs for HTTP status codes (401 = bad token, 404 = wrong repo name, 422 = branch doesn’t exist)

### Terraform errors
- `az account show` — verify you're logged into the correct subscription
- Storage account name conflicts: must be globally unique, lowercase, 3-24 chars
- Function app name conflicts: must be globally unique
- Locked resources: `terraform plan` will show what it wants to change before applying
- **"Dynamic VMs quota = 0"** — VS Enterprise subscription limitation. See [Step 5d](#5d-visual-studio-enterprise--msdn-subscription-notes) for the CLI workaround
- **`workspace_id` cannot be removed** — App Insights was auto-created with a managed workspace. The Terraform config pins `workspace_id` to match; don't remove it
- **`skip_provider_registration = true`** — required for azurerm provider v3.x to avoid timeout errors registering unused providers. Already set in `main.tf`
