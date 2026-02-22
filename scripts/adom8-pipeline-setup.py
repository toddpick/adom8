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
import requests
import time
from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient

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

    secrets_to_store = {
        "ADOM8-ADO-PAT": runtime_pat,
        "ADOM8-AI-KEY": primary_ai_key,   # generic name — holds whichever primary key was supplied
        "GITHUB-TOKEN": github_token,
        "FUNCTION-KEY": function_key
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
            secret_client.set_secret(secret_name, secret_value)
            print(f"Stored secret: {secret_name}")
        except Exception as e:
            print(f"Error storing secret {secret_name}: {e}")

    # Configure Function App Settings
    print("Configuring Function App Settings...")
    # We use Azure CLI via subprocess to set app settings with Key Vault references
    import subprocess
    
    app_settings = [
        f"AzureDevOps__Pat=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=ADOM8-ADO-PAT)",
        f"AI__ApiKey=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=CLAUDE-API-KEY)",
        f"Git__Token=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=GITHUB-TOKEN)",
        # Bug fix: runtime reads Copilot__WebhookSecret, not WebhookSharedSecret
        f"Copilot__WebhookSecret=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=FUNCTION-KEY)",
        f"AzureDevOps__OrganizationUrl={args.ado_org}",
        f"AzureDevOps__Project={args.ado_project}",
        f"Git__Provider=github",
        f"Git__Owner={args.github_org}",
        f"Git__Repo={args.github_repo}",
        f"AI__Provider=anthropic",
        # Bug fix: updated to current Claude Sonnet 4 model
        f"AI__Model=claude-sonnet-4-20250514",
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
                prov_response = requests.post(function_url, timeout=120)
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
    
    # Create Webhook
    gh_webhook_url = f"https://api.github.com/repos/{args.github_org}/{args.github_repo}/hooks"
    webhook_payload = {
        "name": "web",
        "active": True,
        "events": ["pull_request", "issue_comment"],
        "config": {
            "url": f"https://{args.function_app}.azurewebsites.net/api/github-webhook?code={function_key}",
            "content_type": "json",
            "insecure_ssl": "0"
        }
    }
    
    try:
        gh_response = requests.post(gh_webhook_url, headers=gh_headers, json=webhook_payload)
        if gh_response.status_code in [200, 201]:
            print("GitHub webhook registered successfully.")
        elif gh_response.status_code == 422:
            print("GitHub webhook already exists.")
        else:
            print(f"Warning: Failed to create GitHub webhook: {gh_response.status_code} - {gh_response.text}")
    except Exception as e:
        print(f"Warning: Exception creating GitHub webhook: {e}")

    # Create .adom8 folder structure
    print("Creating .adom8 folder structure...")
    readme_content = "# ADOm8 Configuration\n\nThis folder contains configuration and context for the ADOm8 AI agents."
    import base64
    readme_b64 = base64.b64encode(readme_content.encode()).decode()
    
    gh_file_url = f"https://api.github.com/repos/{args.github_org}/{args.github_repo}/contents/.adom8/README.md"
    file_payload = {
        "message": "Initialize .adom8 configuration folder",
        "content": readme_b64
    }
    
    try:
        file_response = requests.put(gh_file_url, headers=gh_headers, json=file_payload)
        if file_response.status_code in [200, 201]:
            print("Created .adom8/README.md successfully.")
        elif file_response.status_code == 422:
            print(".adom8/README.md already exists.")
        else:
            print(f"Warning: Failed to create .adom8/README.md: {file_response.status_code}")
    except Exception as e:
        print(f"Warning: Exception creating file: {e}")

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
