# ADOm8 Pipeline Setup Guide

This guide walks you through the preferred method for onboarding ADOm8 into your Azure DevOps environment using the automated Azure Pipeline.

If you prefer to run scripts locally or deploy manually, please see the [Manual Setup Guide](SETUP-MANUAL.md).

## Prerequisites

Before running the pipeline, you need to gather the following information and create two tokens:

1. **Azure Subscription ID**: The ID of the Azure subscription where resources will be deployed.
2. **Azure DevOps Organization URL**: e.g., `https://dev.azure.com/yourorg` (Must be the full URL, not just the organization name)
3. **Azure DevOps Project Name**: The name of your target project.
4. **GitHub Organization**: The owner of the target repository.
5. **GitHub Repository**: The name of the target repository.
6. **Claude API Key**: Your Anthropic API key.
7. **Resource Group Name**: The desired name for the new Azure Resource Group.
8. **Location**: The Azure region (e.g., `eastus`).
9. **Azure Service Connection**: The name of an existing Azure Resource Manager service connection in your ADO project that has Contributor access to your subscription.
10. **Copilot Enabled (Optional)**: Set pipeline variable `COPILOT_ENABLED` (`true` by default) to delegate coding to GitHub Copilot by default.
11. **MCP Bootstrap Enabled (Optional)**: Set pipeline variable `MCP_BOOTSTRAP_ENABLED` (`true` by default) to create MCP bootstrap guidance files in your GitHub repo.
12. **GitHub Base Branch (Optional)**: Set pipeline variable `GITHUB_BASE_BRANCH` (`main` by default) to choose which branch AI feature branches are created from (for example, `dev`).

### Create Tokens

You need to create two tokens manually. The pipeline will use these to configure everything else.

#### 1. Onboarding PAT (Azure DevOps)
Create a Personal Access Token in Azure DevOps with the following scopes:
- Process
- Project and Team
- Work Items (Read & Write)
- Code (Read & Write)
- Build (Read & Execute)
- Release
- Service Connections

*Note: This token is only used during the pipeline run to create a dedicated runtime PAT and configure the project. You will be instructed to revoke it after the pipeline completes.*

#### 2. GitHub Token
Create a Fine-grained Personal Access Token in GitHub scoped to your target repository with the following permissions:
- Contents (Read/Write)
- Pull requests (Read/Write)
- Issues (Read/Write)
- Webhooks (Read/Write)
- Secrets (Read/Write)

## Running the Pipeline

1. **Import the Pipeline**:
   - In your Azure DevOps project, go to **Pipelines** -> **New pipeline**.
   - Select **Azure Repos Git** (or your repository provider).
   - Select your repository.
   - Choose **Existing Azure Pipelines YAML file**.
   - Select the `adom8-onboarding-pipeline.yml` file from the root of the repository.

2. **Configure Variables**:
   Before running the pipeline, you must define the required variables. Click **Variables** in the top right corner of the pipeline editor and add the following:
   
   - `AZURE_SUBSCRIPTION_ID`
   - `AZURE_DEVOPS_ORG` (full URL, e.g. `https://dev.azure.com/yourorg`)
   - `AZURE_DEVOPS_PROJECT`
   - `GITHUB_ORG`
   - `GITHUB_REPO`
   - `RESOURCE_GROUP_NAME`
   - `LOCATION`
   - `AZURE_SERVICE_CONNECTION`
   - `COPILOT_ENABLED` (optional, defaults to `true`)
   - `MCP_BOOTSTRAP_ENABLED` (optional, defaults to `true`)
   - `GITHUB_BASE_BRANCH` (optional, defaults to `main`; set to `dev` if you want AI feature branches based on your dev branch)
   
   **Secret Variables** (Make sure to check "Keep this value secret"):
   - `ONBOARDING_PAT`
   - `CLAUDE_API_KEY`
   - `GITHUB_TOKEN`
   - `AdoDashboardKey`

3. **Run the Pipeline**:
   - Click **Run**.
   - The pipeline will execute the following stages:
     - **Stage 1**: Deploy Azure Infrastructure (Resource Group, Storage, Key Vault, Function App).
     - **Stage 2**: Create a dedicated adom8 Runtime PAT and store it in Key Vault.
     - **Stage 3**: Store all secrets securely in Key Vault and configure the Function App.
     - **Stage 4**: Customize the ADO Process (create inherited process, custom fields, states, and board rules).
     - **Stage 5**: Configure GitHub (register webhook, create `.adom8` folder).
          - Includes MCP bootstrap guidance files under `.adom8/mcp/` when `MCP_BOOTSTRAP_ENABLED=true`.
     - **Stage 6**: Create an ADO Service Connection to GitHub.
     - **Stage 7**: Run validation checks and output a summary.

    **Hosting model note**: Stage 1 now enforces a Windows Consumption (`Y1`) Function App for `.NET 8` isolated. If a same-name app already exists on Linux, the pipeline deletes and recreates it on Windows to remove hosting drift.

## Post-Setup Steps

Once the pipeline completes successfully:

1. **Review the Summary**: Check the pipeline logs for the final summary, which includes the names of the created resources and the Key Vault URL.
2. **Revoke Onboarding PAT**: You can now safely revoke the `ONBOARDING_PAT` you created in the prerequisites. The pipeline has automatically generated and securely stored a dedicated runtime PAT for the agent.
3. **Copilot Webhook Secret (Auto-Managed)**: No manual secret creation is required. The pipeline auto-generates a secure webhook secret (unless you provide an override), stores it in Key Vault, and configures both the Function App and GitHub webhook to use the same secret.
4. **Configure GitHub Copilot Permissions**: If you are using GitHub Copilot, ensure the agent has the necessary permissions on the repository (this cannot be automated via API).
5. **Register MCP Servers in GitHub Copilot (Required for MCP Tools)**:
   - Open GitHub repository settings → **Copilot** → **Coding agent** → **MCP configuration**.
   - Copy/paste `.adom8/mcp/mcp.template.json` into the MCP configuration box.
   - If you enable the optional `ado` MCP server, add Copilot environment secret `COPILOT_MCP_AZURE_DEVOPS_PAT` before starting sessions.
   - This UI registration step is manual and required; pipeline bootstrap prepares the config file but cannot populate the GitHub MCP textbox via API.
6. **Access the Dashboard**: Your dashboard is available at the Static Web App URL (found in the pipeline summary). Use the `AdoDashboardKey` you provided to log in. The Static Web App resource name is derived from your `AZURE_DEVOPS_PROJECT`, but Azure still generates the default `*.azurestaticapps.net` hostname. Configure a custom domain in the Azure Portal if you want a friendly URL.
7. **Default Field Values (Auto-Enforced)**: The pipeline now enforces `Custom.AutonomyLevel` as a picklist (`1-5`) with default `3 - Review & Pause`, and sets `Custom.AIMinimumReviewScore` default to `85` for User Story work items.
8. **Start Using ADOm8**: Visit [adom8.dev/get-started](https://adom8.dev/get-started) for instructions on creating your first story and triggering the AI agent.

## MCP Automation Matrix

### Automated by the onboarding pipeline
- Create repository MCP bootstrap guidance in `.adom8/mcp/README.md`.
- Create starter template at `.adom8/mcp/mcp.template.json`.
- Include a schema-valid GitHub Copilot Coding Agent MCP server template (`github-mcp-server`, optional `ado`).
- Include guidance for Phase 1 ADOm8 REST bridge endpoints (`/api/mcp/set-stage`, `/api/mcp/add-comment`, `/api/mcp/stage-event`).
- Keep these files idempotent across re-runs.

### Not automatable via Azure DevOps pipeline (manual)
- Installing MCP clients/tools on developer machines.
- Interactive sign-in/authorization flows inside MCP clients.
- Organization/admin approvals that require UI confirmation in GitHub or Azure DevOps.

Use the generated `.adom8/mcp/*` files as your baseline and complete local client wiring manually.

Advanced Copilot checkpoint tuning is optional and documented in `TROUBLESHOOTING.md` under **Optional Fine-Tuning: Copilot Checkpoint Enforcement**.

## Re-run Checklist (MyCreditPlan / Existing Projects)

Use this quick checklist after re-running `adom8-onboarding-pipeline.yml` to confirm the latest runtime behavior is active.

1. **Initialize Codebase story no longer auto-fails for missing AC**
   - Trigger **Initialize Codebase** from the dashboard.
   - Open the created story and confirm **Acceptance Criteria** is populated (not blank).
   - Move state to `AI Agent` and verify it proceeds to `Coding Agent` instead of immediately returning to `Needs Revision`.

2. **Current AI Agent remains on Coding during Copilot delegation**
   - When Coding delegates to GitHub Copilot, verify `Current AI Agent` stays `Coding Agent` while the Copilot run is in progress.
   - It should no longer clear to blank during the wait period.

3. **Copilot completion advances without reviewer request dependency**
   - When GitHub emits `pull_request` action `ready_for_review`, pipeline should reconcile and continue even if no reviewer was requested.
   - Expected progression on Copilot path: `Coding Agent` → skip Testing → `Review Agent`.

4. **Webhook wiring check**
   - In GitHub repo settings, confirm webhook events include `pull_request` and destination is:
     - `https://<function-app>.azurewebsites.net/api/copilot-webhook?code=<function-key>`
   - In Azure DevOps Service Hooks, confirm work-item state hook points to:
     - `https://<function-app>.azurewebsites.net/api/webhook?code=<function-key>`

If any check fails, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md) sections for Copilot draft/reconciliation and resume behavior.
