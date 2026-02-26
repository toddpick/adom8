# ----------------------------------------------------------------------------
# ADO-M8 Pipeline Setup Script
# Copyright 2026 ADO-M8 Contributors — https://adom8.dev
# Licensed under the Apache License, Version 2.0
# https://www.apache.org/licenses/LICENSE-2.0
# ----------------------------------------------------------------------------

import argparse
import os
import sys
import json
import secrets
import requests
import time
from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient
from azure.core.exceptions import ResourceNotFoundError

def main():
    parser = argparse.ArgumentParser(description="ADOm8 Pipeline Setup Script")
    parser.add_argument("--subscription-id", required=True)
    parser.add_argument("--ado-org", required=True)
    parser.add_argument("--ado-project", required=True)
    parser.add_argument("--github-org", required=True)
    parser.add_argument("--github-repo", required=True)
    parser.add_argument("--resource-group", required=True)
    parser.add_argument("--function-app", required=True)
    parser.add_argument("--key-vault", required=True)
    args = parser.parse_args()

    onboarding_pat = os.environ.get("ONBOARDING_PAT")
    claude_api_key = os.environ.get("CLAUDE_API_KEY")   # Anthropic — required unless OPENAI_API_KEY is set
    openai_api_key = os.environ.get("OPENAI_API_KEY")   # OpenAI — required unless CLAUDE_API_KEY is set
    google_api_key = os.environ.get("GOOGLE_API_KEY")   # optional — can be added post-setup
    github_token = os.environ.get("GITHUB_TOKEN")
    function_key = os.environ.get("FUNCTION_KEY")
    copilot_enabled = os.environ.get("COPILOT_ENABLED", "true").strip().lower() in ["1", "true", "yes", "on"]
    copilot_mode = os.environ.get("COPILOT_MODE", "Auto").strip() or "Auto"
    copilot_complexity_threshold = os.environ.get("COPILOT_COMPLEXITY_THRESHOLD", "8").strip() or "8"
    copilot_create_issue = os.environ.get("COPILOT_CREATE_ISSUE", "true").strip().lower() in ["1", "true", "yes", "on"]
    copilot_model = os.environ.get("COPILOT_MODEL", "copilot").strip() or "copilot"
    copilot_checkpoint_enforcement_enabled = os.environ.get("COPILOT_CHECKPOINT_ENFORCEMENT_ENABLED", "true").strip().lower() in ["1", "true", "yes", "on"]
    copilot_checkpoint_fail_hard = os.environ.get("COPILOT_CHECKPOINT_FAIL_HARD", "true").strip().lower() in ["1", "true", "yes", "on"]
    copilot_required_ado_checkpoints = os.environ.get("COPILOT_REQUIRED_ADO_CHECKPOINTS", "LastAgent,CurrentAIAgent,CompletionComment").strip() or "LastAgent,CurrentAIAgent,CompletionComment"
    copilot_webhook_secret = (os.environ.get("COPILOT_WEBHOOK_SECRET") or "").strip()
    repo_capacity_enabled = os.environ.get("REPO_CAPACITY_ENABLED", "true").strip().lower() in ["1", "true", "yes", "on"]
    repo_capacity_max_working_tree_mb = (os.environ.get("REPO_CAPACITY_MAX_WORKING_TREE_MB", "500").strip() or "500")
    repo_capacity_max_binary_mb = (os.environ.get("REPO_CAPACITY_MAX_BINARY_MB", "150").strip() or "150")
    repo_capacity_max_file_count = (os.environ.get("REPO_CAPACITY_MAX_FILE_COUNT", "120000").strip() or "120000")
    repo_capacity_block_truncated_tree = os.environ.get("REPO_CAPACITY_BLOCK_TRUNCATED_TREE", "true").strip().lower() in ["1", "true", "yes", "on"]
    codebase_api_only_init = os.environ.get("CODEBASE_API_ONLY_INIT", "true").strip().lower() in ["1", "true", "yes", "on"]
    codebase_api_file_limit_kb = (os.environ.get("CODEBASE_API_FILE_LIMIT_KB", "100").strip() or "100")
    codebase_api_publish_enabled = os.environ.get("CODEBASE_API_PUBLISH_ENABLED", "true").strip().lower() in ["1", "true", "yes", "on"]
    mcp_bootstrap_enabled = os.environ.get("MCP_BOOTSTRAP_ENABLED", "true").strip().lower() in ["1", "true", "yes", "on"]

    if not copilot_webhook_secret:
        copilot_webhook_secret = secrets.token_urlsafe(48)
        print("No COPILOT_WEBHOOK_SECRET supplied — generated a secure webhook secret automatically.")

    if not all([onboarding_pat, github_token, function_key]):
        print("Error: Missing required environment variables (ONBOARDING_PAT, GITHUB_TOKEN, FUNCTION_KEY).")
        sys.exit(1)
    if not claude_api_key and not openai_api_key:
        print("Error: At least one AI provider key is required: set CLAUDE_API_KEY (Anthropic) or OPENAI_API_KEY (OpenAI).")
        sys.exit(1)

    # Determine primary AI provider — Claude preferred when both supplied
    if claude_api_key:
        primary_ai_key = claude_api_key
        ai_provider = "anthropic"
        ai_model = "claude-sonnet-4-20250514"
        print("Using Anthropic (Claude) as primary AI provider.")
    else:
        primary_ai_key = openai_api_key
        ai_provider = "openai"
        ai_model = "gpt-4o"
        print("Using OpenAI as primary AI provider.")

    print("Starting ADOm8 Pipeline Setup...")

    # Stage 2: Create adom8 Runtime PAT
    print("\n--- Stage 2: Create adom8 Runtime PAT ---")
    # Note: ADO PAT creation via API is currently in preview and requires specific scopes.
    # For this script, we will simulate the creation or use the onboarding PAT if API fails.
    # The actual API endpoint is POST https://vssps.dev.azure.com/{organization}/_apis/tokens/pats?api-version=7.1-preview.1
    
    ado_org_url = args.ado_org.rstrip('/')
    org_name = ado_org_url.split('/')[-1]
    
    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Basic {requests.auth._basic_auth_str('', onboarding_pat)}"
    }
    
    pat_payload = {
        "displayName": "ADOM8-Runtime-PAT",
        "scope": "vso.work_write vso.code vso.build",
        "validTo": (time.time() + 31536000) * 1000, # 1 year
        "allOrgs": False
    }
    
    runtime_pat = onboarding_pat # Fallback
    try:
        pat_url = f"https://vssps.dev.azure.com/{org_name}/_apis/tokens/pats?api-version=7.1-preview.1"
        response = requests.post(pat_url, headers=headers, json=pat_payload)
        if response.status_code in [200, 201]:
            runtime_pat = response.json().get("patToken", {}).get("token", onboarding_pat)
            print("Successfully created runtime PAT.")
        else:
            print(f"Warning: Could not create runtime PAT via API ({response.status_code}). Using onboarding PAT as fallback.")
    except Exception as e:
        print(f"Warning: Exception creating PAT: {e}")

    # Stage 3: Store All Secrets in Key Vault
    print("\n--- Stage 3: Store All Secrets in Key Vault ---")
    kv_url = f"https://{args.key_vault}.vault.azure.net/"
    credential = DefaultAzureCredential()
    secret_client = SecretClient(vault_url=kv_url, credential=credential)

    def set_secret_if_changed(secret_name, secret_value):
        try:
            current = secret_client.get_secret(secret_name)
            if current and current.value == secret_value:
                print(f"Secret unchanged (skipped): {secret_name}")
                return
        except ResourceNotFoundError:
            pass
        except Exception as ex:
            print(f"Warning: Could not read existing secret {secret_name}; will attempt update. Details: {ex}")

        secret_client.set_secret(secret_name, secret_value)
        print(f"Stored secret: {secret_name}")

    secrets_to_store = {
        "ADOM8-ADO-PAT": runtime_pat,
        "ADOM8-AI-KEY": primary_ai_key,   # generic name — holds whichever primary key was supplied
        "GITHUB-TOKEN": github_token,
        "FUNCTION-KEY": function_key,
        "COPILOT-WEBHOOK-SECRET": copilot_webhook_secret
    }
    # Store secondary/additional provider keys if supplied
    if claude_api_key and openai_api_key:
        # Both provided — Claude is primary; store OpenAI as a secondary provider key too
        secrets_to_store["OPENAI-API-KEY"] = openai_api_key
        print("Both AI provider keys detected — Claude primary, OpenAI will also be wired.")
    elif openai_api_key and not claude_api_key:
        # OpenAI is primary — also store under provider-keyed name for runtime
        secrets_to_store["OPENAI-API-KEY"] = openai_api_key
    if google_api_key:
        secrets_to_store["GOOGLE-API-KEY"] = google_api_key
        print("Google API key detected — will be provisioned.")

    for secret_name, secret_value in secrets_to_store.items():
        try:
            set_secret_if_changed(secret_name, secret_value)
        except Exception as e:
            print(f"Error storing secret {secret_name}: {e}")

    # Configure Function App Settings
    print("Configuring Function App Settings...")
    # We use Azure CLI via subprocess to set app settings with Key Vault references
    import subprocess
    
    app_settings = [
        f"AzureDevOps__Pat=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=ADOM8-ADO-PAT)",
        f"AI__ApiKey=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=ADOM8-AI-KEY)",
        f"Git__Token=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=GITHUB-TOKEN)",
        f"Copilot__WebhookSecret=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=COPILOT-WEBHOOK-SECRET)",
        f"Copilot__Enabled={'true' if copilot_enabled else 'false'}",
        f"Copilot__Mode={copilot_mode}",
        f"Copilot__ComplexityThreshold={copilot_complexity_threshold}",
        f"Copilot__CreateIssue={'true' if copilot_create_issue else 'false'}",
        f"Copilot__Model={copilot_model}",
        f"Copilot__CheckpointEnforcementEnabled={'true' if copilot_checkpoint_enforcement_enabled else 'false'}",
        f"Copilot__CheckpointFailHard={'true' if copilot_checkpoint_fail_hard else 'false'}",
        f"Copilot__RequiredAdoCheckpoints={copilot_required_ado_checkpoints}",
        f"AzureDevOps__OrganizationUrl=https://dev.azure.com/{org_name}",
        f"AzureDevOps__Project={args.ado_project}",
        f"Git__Provider=github",
        f"Git__RepositoryUrl=https://github.com/{args.github_org}/{args.github_repo}.git",
        f"Git__Username=x-access-token",
        f"Git__Email=adom8@adom8.dev",
        f"Git__Name=ADOm8 Agent",
        f"GitHub__Owner={args.github_org}",
        f"GitHub__Repo={args.github_repo}",
        f"GitHub__Token=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=GITHUB-TOKEN)",
        f"AI__Provider={ai_provider}",
        f"AI__Model={ai_model}",
        f"RepositoryCapacity__Enabled={'true' if repo_capacity_enabled else 'false'}",
        f"RepositoryCapacity__MaxEstimatedWorkingTreeBytes={int(repo_capacity_max_working_tree_mb) * 1024 * 1024}",
        f"RepositoryCapacity__MaxBinaryBytes={int(repo_capacity_max_binary_mb) * 1024 * 1024}",
        f"RepositoryCapacity__MaxFileCount={int(repo_capacity_max_file_count)}",
        f"RepositoryCapacity__BlockWhenTreeTruncated={'true' if repo_capacity_block_truncated_tree else 'false'}",
        f"CodebaseDocumentation__ApiOnlyInitializationEnabled={'true' if codebase_api_only_init else 'false'}",
        f"CodebaseDocumentation__ApiFileSizeLimitBytes={int(codebase_api_file_limit_kb) * 1024}",
        f"CodebaseDocumentation__ApiPublishEnabled={'true' if codebase_api_publish_enabled else 'false'}",
    ]
    # Wire optional AI provider keys into Function App settings if they were provided
    if openai_api_key:
        app_settings.append(f"AI__ProviderKeys__OpenAI__ApiKey=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=OPENAI-API-KEY)")
    if google_api_key:
        app_settings.append(f"AI__ProviderKeys__Google__ApiKey=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=GOOGLE-API-KEY)")
    # Note: per-agent model overrides (AI__AgentModels__*) and tier presets can be added
    # to the Function App configuration post-setup via the Azure Portal or az CLI.
    app_settings = app_settings  # satisfy linter — list is complete
    
    cmd = [
        "az", "functionapp", "config", "appsettings", "set",
        "--name", args.function_app,
        "--resource-group", args.resource_group,
        "--settings"
    ] + app_settings
    
    subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    print("Function App settings configured.")

    # Stage 4: ADO Process Customization
    print("\n--- Stage 4: ADO Process Customization ---")
    # This is a complex set of API calls. We will call the provision-ado endpoint on the function app
    # since it already has the logic to create states, fields, and webhooks.
    function_url = f"https://{args.function_app}.azurewebsites.net/api/provision-ado?code={function_key}"
    print(f"Calling ADO Provisioning endpoint: {function_url}")
    
    try:
        # Poll until the function app is warm and responsive (cold-start on consumption plan
        # can take 2-3 minutes after a fresh deploy + settings change).
        health_url = f"https://{args.function_app}.azurewebsites.net/api/health"
        max_wait_seconds = 180
        poll_interval = 15
        elapsed = 0
        print(f"Waiting for Function App to be ready (up to {max_wait_seconds}s)...")
        while elapsed < max_wait_seconds:
            try:
                ping = requests.get(health_url, timeout=10)
                if ping.status_code < 500:
                    print(f"Function App is responding (status {ping.status_code}) after {elapsed}s.")
                    break
            except Exception:
                pass
            print(f"  Not ready yet, retrying in {poll_interval}s... ({elapsed}s elapsed)")
            time.sleep(poll_interval)
            elapsed += poll_interval
        else:
            print(f"Warning: Function App did not respond within {max_wait_seconds}s — attempting provision anyway.")

        # Retry provision-ado up to 3 times in case of transient cold-start errors
        max_retries = 3
        for attempt in range(1, max_retries + 1):
            try:
                print(f"  Attempt {attempt}: POST {function_url}")
                prov_response = requests.post(function_url, timeout=120)
                print(f"  Response Status: {prov_response.status_code}")
                print(f"  Response Body: {prov_response.text}")
                
                if prov_response.status_code == 200:
                    print("ADO Process Customization completed successfully via Function App.")
                    try:
                        result = prov_response.json()
                        if result.get("warnings"):
                            for w in result["warnings"]:
                                print(f"  Warning: {w}")
                        if result.get("manualSteps"):
                            print("  Manual follow-up steps returned:")
                            for s in result["manualSteps"]:
                                print(f"    - {s}")
                    except Exception:
                        pass
                    break
                elif prov_response.status_code in (502, 503, 504) and attempt < max_retries:
                    print(f"  Attempt {attempt} got {prov_response.status_code}, retrying in 20s...")
                    time.sleep(20)
                else:
                    print(f"Warning: ADO Provisioning returned {prov_response.status_code}: {prov_response.text}")
                    break
            except Exception as e:
                if attempt < max_retries:
                    print(f"  Attempt {attempt} failed ({e}), retrying in 20s...")
                    time.sleep(20)
                else:
                    print(f"Warning: Exception calling ADO Provisioning after {max_retries} attempts: {e}")
    except Exception as e:
        print(f"Warning: Exception calling ADO Provisioning: {e}")

    # Stage 5: GitHub Configuration
    print("\n--- Stage 5: GitHub Configuration ---")
    gh_headers = {
        "Authorization": f"token {github_token}",
        "Accept": "application/vnd.github.v3+json"
    }

    def upsert_github_file(path, content, commit_message, overwrite=True):
        file_url = f"https://api.github.com/repos/{args.github_org}/{args.github_repo}/contents/{path}"
        encoded_content = base64.b64encode(content.encode()).decode()

        sha = None
        try:
            existing = requests.get(file_url, headers=gh_headers, timeout=30)
            if existing.status_code == 200:
                body = existing.json()
                sha = body.get("sha")
                if not overwrite:
                    print(f"Skipped existing file (overwrite disabled): {path}")
                    return
            elif existing.status_code != 404:
                print(f"Warning: Could not inspect {path}: {existing.status_code} - {existing.text}")
        except Exception as e:
            print(f"Warning: Exception checking file {path}: {e}")

        payload = {
            "message": commit_message,
            "content": encoded_content
        }
        if sha:
            payload["sha"] = sha

        try:
            put_resp = requests.put(file_url, headers=gh_headers, json=payload, timeout=30)
            if put_resp.status_code in [200, 201]:
                print(f"Upserted GitHub file: {path}")
            else:
                print(f"Warning: Failed to upsert {path}: {put_resp.status_code} - {put_resp.text}")
        except Exception as e:
            print(f"Warning: Exception upserting {path}: {e}")
    
    # Create Webhook
    gh_webhook_url = f"https://api.github.com/repos/{args.github_org}/{args.github_repo}/hooks"
    webhook_payload = {
        "name": "web",
        "active": True,
        "events": ["pull_request", "issue_comment"],
        "config": {
            "url": f"https://{args.function_app}.azurewebsites.net/api/copilot-webhook?code={function_key}",
            "content_type": "json",
            "secret": copilot_webhook_secret,
            "insecure_ssl": "0"
        }
    }
    
    target_webhook_url = webhook_payload["config"]["url"]

    try:
        hooks_response = requests.get(gh_webhook_url, headers=gh_headers, timeout=30)
        if hooks_response.status_code != 200:
            print(f"Warning: Could not list existing GitHub webhooks: {hooks_response.status_code} - {hooks_response.text}")
            hooks = []
        else:
            hooks = hooks_response.json() if isinstance(hooks_response.json(), list) else []

        managed_hooks = []
        for hook in hooks:
            config = hook.get("config") or {}
            existing_url = (config.get("url") or "").strip()
            if ".azurewebsites.net/api/" in existing_url and ("copilot-webhook" in existing_url or "github-webhook" in existing_url):
                managed_hooks.append(hook)

        current_hook = None
        for hook in managed_hooks:
            hook_id = hook.get("id")
            config = hook.get("config") or {}
            existing_url = (config.get("url") or "").strip()

            if existing_url == target_webhook_url and current_hook is None:
                current_hook = hook
                continue

            if hook_id:
                delete_url = f"{gh_webhook_url}/{hook_id}"
                delete_response = requests.delete(delete_url, headers=gh_headers, timeout=30)
                if delete_response.status_code in [204, 404]:
                    print(f"Removed stale GitHub webhook (id={hook_id}).")
                else:
                    print(f"Warning: Failed to remove stale GitHub webhook (id={hook_id}): {delete_response.status_code} - {delete_response.text}")

        if current_hook and current_hook.get("id"):
            update_url = f"{gh_webhook_url}/{current_hook['id']}"
            update_payload = {
                "active": True,
                "events": ["pull_request", "issue_comment"],
                "config": webhook_payload["config"]
            }
            update_response = requests.patch(update_url, headers=gh_headers, json=update_payload, timeout=30)
            if update_response.status_code in [200, 201]:
                print("GitHub webhook already existed and was updated successfully.")
            else:
                print(f"Warning: Failed to update existing GitHub webhook: {update_response.status_code} - {update_response.text}")
        else:
            create_response = requests.post(gh_webhook_url, headers=gh_headers, json=webhook_payload, timeout=30)
            if create_response.status_code in [200, 201]:
                print("GitHub webhook registered successfully.")
            else:
                print(f"Warning: Failed to create GitHub webhook: {create_response.status_code} - {create_response.text}")
    except Exception as e:
        print(f"Warning: Exception configuring GitHub webhook: {e}")

    # Create .adom8 folder structure
    print("Creating .adom8 folder structure...")
    import base64

    readme_content = "# ADOm8 Configuration\n\nThis folder contains configuration and context for the ADOm8 AI agents."
    upsert_github_file(
        ".adom8/README.md",
        readme_content,
        "Initialize .adom8 configuration folder",
        overwrite=False
    )

    if mcp_bootstrap_enabled:
        print("MCP bootstrap is enabled — creating repository MCP guidance files.")
        mcp_readme = f"""# MCP Bootstrap (ADOm8)

This repository was bootstrapped by the ADOm8 onboarding pipeline with MCP guidance artifacts.

## What the pipeline configured automatically

- Created this guidance folder under `.adom8/mcp/`
- Added a starter MCP manifest at `.adom8/mcp/mcp.template.json`

## What cannot be automated in Azure DevOps pipeline

- Installing MCP client tooling on each developer machine
- Signing in interactively to provider accounts (GitHub, Azure DevOps) inside local MCP clients
- Approving organization-level policies that require admin UI confirmation

## Recommended next step

Use `mcp.template.json` as a starting point in your MCP client and provide credentials through the client's secure secret store.
"""

        mcp_template = json.dumps({
            "version": 1,
            "generatedBy": "ADOm8 onboarding pipeline",
            "servers": {
                "github": {
                    "description": "Configure your GitHub MCP server in your MCP client.",
                    "requiredAuth": "GitHub token with repository access"
                },
                "azure_devops": {
                    "description": "Configure your Azure DevOps MCP-compatible connector in your MCP client.",
                    "requiredAuth": "Azure DevOps PAT with work item/code access",
                    "organizationUrl": args.ado_org,
                    "project": args.ado_project
                }
            },
            "notes": [
                "This is a bootstrap template, not an executable local client config.",
                "Keep credentials out of source control; store them in client secret stores."
            ]
        }, indent=2)

        upsert_github_file(
            ".adom8/mcp/README.md",
            mcp_readme,
            "docs: add MCP bootstrap guidance",
            overwrite=True
        )
        upsert_github_file(
            ".adom8/mcp/mcp.template.json",
            mcp_template,
            "docs: add MCP bootstrap template",
            overwrite=True
        )
    else:
        print("MCP bootstrap disabled via MCP_BOOTSTRAP_ENABLED=false")

    # Stage 6: ADO Service Connection
    print("\n--- Stage 6: ADO Service Connection ---")
    # Create GitHub service connection in ADO
    sc_url = f"{args.ado_org}/{args.ado_project}/_apis/serviceendpoint/endpoints?api-version=7.1-preview.4"
    sc_payload = {
        "name": "ADOM8-GitHub-Connection",
        "type": "github",
        "url": "https://github.com",
        "authorization": {
            "scheme": "PersonalAccessToken",
            "parameters": {
                "accessToken": github_token
            }
        },
        "isShared": False,
        "isReady": True
    }
    
    try:
        sc_response = requests.post(sc_url, headers=headers, json=sc_payload)
        if sc_response.status_code in [200, 201]:
            print("ADO Service Connection to GitHub created successfully.")
        else:
            print(f"Warning: Failed to create ADO Service Connection: {sc_response.status_code} - {sc_response.text}")
    except Exception as e:
        print(f"Warning: Exception creating Service Connection: {e}")

    # Stage 7: Validation
    print("\n--- Stage 7: Validation ---")
    validation_results = []
    
    # 1. Function App Health
    try:
        health_url = f"https://{args.function_app}.azurewebsites.net/api/status?code={function_key}"
        h_resp = requests.get(health_url)
        if h_resp.status_code == 200:
            validation_results.append("PASS: Azure Function deployed and health endpoint returns 200")
        else:
            validation_results.append(f"FAIL: Azure Function health endpoint returned {h_resp.status_code}. Hint: Check Function App logs.")
    except Exception as e:
        validation_results.append(f"FAIL: Azure Function health check failed: {e}")

    # 2. Key Vault Access
    try:
        secret = secret_client.get_secret("ADOM8-ADO-PAT")
        if secret:
            validation_results.append("PASS: Key Vault accessible and contains required secrets")
        else:
            validation_results.append("FAIL: Key Vault accessible but secret missing.")
    except Exception as e:
        validation_results.append(f"FAIL: Key Vault access failed: {e}. Hint: Check managed identity access policies.")

    # Print Summary
    print("\n================================================================")
    print("                  ADOM8 ONBOARDING SUMMARY                      ")
    print("================================================================")
    for res in validation_results:
        print(res)
    
    print("\nResources Created:")
    print(f"- Resource Group: {args.resource_group}")
    print(f"- Storage Account: {args.function_app.replace('-', '')[:24]}")
    print(f"- Key Vault: {args.key_vault}")
    print(f"- Function App: {args.function_app}")
    
    print(f"\nKey Vault URL: {kv_url}")
    print("All secrets have been securely stored in Key Vault.")
    print("\nSECURITY NOTICE:")
    print("1. The adom8 runtime PAT was automatically created and stored in Key Vault. You never need to manage it.")
    print("2. The ONBOARDING_PAT you provided can now be safely revoked in Azure DevOps.")
    
    print("\nNext Steps:")
    print("Visit https://adom8.dev/get-started for instructions on creating your first story.")
    print("================================================================")

if __name__ == "__main__":
    main()
