# ADOm8 Setup Guide (Pipeline Method)

The recommended way to deploy ADOm8 is using the automated Azure DevOps pipeline. This pipeline provisions all necessary Azure infrastructure, configures your ADO project, sets up GitHub webhooks, and securely stores all secrets in Azure Key Vault.

## Prerequisites

Before running the pipeline, you need:

1. **Azure Subscription**: You must have Contributor access to an Azure subscription.
2. **Azure DevOps Organization & Project**: You must be a Project Administrator.
3. **GitHub Repository**: You must have admin access to the target repository.
4. **AI Provider API Key**: An API key for Claude (Anthropic), OpenAI, or Azure OpenAI.

## Step 1: Create Required Tokens

You need to create two Personal Access Tokens (PATs) before running the pipeline:

### 1. Azure DevOps Onboarding PAT
This is a temporary, elevated PAT used *only* by the pipeline to set up the environment. It will be used to create a restricted runtime PAT and then you can revoke it.
- Go to your ADO profile → Personal access tokens → + New Token.
- Name: `ADOm8 Onboarding`
- Expiration: 30 days (you will revoke it immediately after setup)
- Scopes (Custom defined):
  - **Project and Team**: Read, write, & manage
  - **Work Items**: Read, write, & manage
  - **Code**: Read, write, & manage
  - **Build**: Read & execute
  - **Release**: Read, write, & manage
  - **Service Connections**: Read, query, & manage
  - **Tokens**: Read & manage (Required to generate the runtime PAT)

### 2. GitHub Token
This token is used by the AI agents to read code, create branches, and open Pull Requests.
- Go to GitHub Settings → Developer settings → Personal access tokens → Fine-grained tokens.
- Name: `ADOm8 Agent`
- Repository access: Only select your target repository.
- Permissions:
  - **Contents**: Read and write
  - **Pull requests**: Read and write
  - **Issues**: Read and write
  - **Metadata**: Read-only (auto-granted)

## Step 2: Import the Pipeline

1. In your Azure DevOps project, go to **Pipelines** → **New pipeline**.
2. Select **Azure Repos Git** (or GitHub if your repo is there).
3. Select your repository.
4. Choose **Existing Azure Pipelines YAML file**.
5. Select the `/adom8-onboarding-pipeline.yml` file from the repository.

## Step 3: Configure Pipeline Variables

Before running the pipeline, you must configure the required variables.

1. Click **Variables** in the top right of the pipeline run screen.
2. Add the following **Secret** variables (click the lock icon for each):
   - `ONBOARDING_PAT`: The ADO PAT you created in Step 1.
   - `GITHUB_TOKEN`: The GitHub PAT you created in Step 1.
   - `CLAUDE_API_KEY`: Your AI provider API key.
3. The pipeline will prompt you for the following parameters when you click Run:
   - `AZURE_SERVICE_CONNECTION`: The name of your Azure Resource Manager service connection in ADO.
   - `AZURE_SUBSCRIPTION_ID`: Your Azure Subscription ID.
   - `AZURE_DEVOPS_ORG`: Your ADO organization URL (e.g., `https://dev.azure.com/yourorg`).
   - `AZURE_DEVOPS_PROJECT`: Your ADO project name.
   - `GITHUB_ORG`: Your GitHub organization or username.
   - `GITHUB_REPO`: Your target repository name.
   - `RESOURCE_GROUP_NAME`: The name for the new Azure Resource Group (default: `rg-adom8-agents`).
   - `LOCATION`: The Azure region to deploy to (default: `eastus`).

## Step 4: Run the Pipeline

Click **Run**. The pipeline will execute the following stages:

1. **Azure Infrastructure**: Creates the Resource Group, Storage Account, Key Vault, and Function App.
2. **Create adom8 Runtime PAT**: Uses your onboarding PAT to generate a restricted 1-year PAT for the agents.
3. **Store Secrets**: Securely stores all API keys and tokens in the Key Vault.
4. **ADO Process Customization**: Creates an inherited process (if needed), adds the `Custom.CurrentAgentTask` field, creates the `AI Agent` state, and configures board card styling rules.
5. **GitHub Configuration**: Registers the webhook to trigger the agents on PR events and creates the `.adom8` configuration folder.
6. **ADO Service Connection**: Creates a service connection to GitHub.
7. **Validation**: Runs end-to-end checks to ensure everything is configured correctly.

## Step 5: Post-Setup

1. **Review the Summary**: Check the pipeline logs for the final summary output. It will list all created resources and validation results.
2. **Revoke Onboarding PAT**: You can now safely delete the `ADOm8 Onboarding` PAT from your ADO profile. The pipeline automatically created and stored a restricted runtime PAT in Key Vault.
3. **Configure GitHub Copilot (Optional)**: If you are using GitHub Copilot, ensure the agent permissions are configured on your repository.

You are now ready to create your first User Story and assign it to the `AI Agent` state!

---
*Note: If you prefer to run the setup manually from your local machine, see [SETUP-MANUAL.md](SETUP-MANUAL.md).*