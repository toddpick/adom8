# ADOm8 Pipeline Setup Guide

This guide walks you through the preferred method for onboarding ADOm8 into your Azure DevOps environment using the automated Azure Pipeline.

If you prefer to run scripts locally or deploy manually, please see the [Manual Setup Guide](SETUP-MANUAL.md).

## Prerequisites

Before running the pipeline, you need to gather the following information and create two tokens:

1. **Azure Subscription ID**: The ID of the Azure subscription where resources will be deployed.
2. **Azure DevOps Organization URL**: e.g., `https://dev.azure.com/yourorg`
3. **Azure DevOps Project Name**: The name of your target project.
4. **GitHub Organization**: The owner of the target repository.
5. **GitHub Repository**: The name of the target repository.
6. **Claude API Key**: Your Anthropic API key.
7. **Resource Group Name**: The desired name for the new Azure Resource Group.
8. **Location**: The Azure region (e.g., `eastus`).
9. **Azure Service Connection**: The name of an existing Azure Resource Manager service connection in your ADO project that has Contributor access to your subscription.

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
   - `AZURE_DEVOPS_ORG`
   - `AZURE_DEVOPS_PROJECT`
   - `GITHUB_ORG`
   - `GITHUB_REPO`
   - `RESOURCE_GROUP_NAME`
   - `LOCATION`
   - `AZURE_SERVICE_CONNECTION`
   
   **Secret Variables** (Make sure to check "Keep this value secret"):
   - `ONBOARDING_PAT`
   - `CLAUDE_API_KEY`
   - `GITHUB_TOKEN`

3. **Run the Pipeline**:
   - Click **Run**.
   - The pipeline will execute the following stages:
     - **Stage 1**: Deploy Azure Infrastructure (Resource Group, Storage, Key Vault, Function App).
     - **Stage 2**: Create a dedicated adom8 Runtime PAT and store it in Key Vault.
     - **Stage 3**: Store all secrets securely in Key Vault and configure the Function App.
     - **Stage 4**: Customize the ADO Process (create inherited process, custom fields, states, and board rules).
     - **Stage 5**: Configure GitHub (register webhook, create `.adom8` folder).
     - **Stage 6**: Create an ADO Service Connection to GitHub.
     - **Stage 7**: Run validation checks and output a summary.

## Post-Setup Steps

Once the pipeline completes successfully:

1. **Review the Summary**: Check the pipeline logs for the final summary, which includes the names of the created resources and the Key Vault URL.
2. **Revoke Onboarding PAT**: You can now safely revoke the `ONBOARDING_PAT` you created in the prerequisites. The pipeline has automatically generated and securely stored a dedicated runtime PAT for the agent.
3. **Configure GitHub Copilot Permissions**: If you are using GitHub Copilot, ensure the agent has the necessary permissions on the repository (this cannot be automated via API).
4. **Start Using ADOm8**: Visit [adom8.dev/get-started](https://adom8.dev/get-started) for instructions on creating your first story and triggering the AI agent.
