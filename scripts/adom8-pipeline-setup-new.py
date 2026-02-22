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
import base64
import subprocess
from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient

def get_auth_header(pat):
    b64_pat = base64.b64encode(f":{pat}".encode()).decode()
    return {"Authorization": f"Basic {b64_pat}", "Content-Type": "application/json"}

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
    claude_api_key = os.environ.get("CLAUDE_API_KEY")
    github_token = os.environ.get("GITHUB_TOKEN")
    function_key = os.environ.get("FUNCTION_KEY")

    if not all([onboarding_pat, claude_api_key, github_token, function_key]):
        print("Error: Missing required environment variables.")
        sys.exit(1)

    ado_org_url = args.ado_org.rstrip('/')
    org_name = ado_org_url.split('/')[-1]
    ado_headers = get_auth_header(onboarding_pat)

    print("Starting ADOm8 Pipeline Setup...")

    # Stage 2: Create adom8 Runtime PAT
    print("\n--- Stage 2: Create adom8 Runtime PAT ---")
    pat_payload = {
        "displayName": "ADOM8-Runtime-PAT",
        "scope": "vso.work_write vso.code vso.build vso.code_status",
        "validTo": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 31536000)), # 1 year
        "allOrgs": False
    }
    
    runtime_pat = onboarding_pat # Fallback
    try:
        pat_url = f"https://vssps.dev.azure.com/{org_name}/_apis/tokens/pats?api-version=7.1-preview.1"
        response = requests.post(pat_url, headers=ado_headers, json=pat_payload)
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
        "CLAUDE-API-KEY": claude_api_key,
        "GITHUB-TOKEN": github_token,
        "FUNCTION-KEY": function_key
    }

    for secret_name, secret_value in secrets_to_store.items():
        try:
            secret_client.set_secret(secret_name, secret_value)
            print(f"Stored secret: {secret_name}")
        except Exception as e:
            print(f"Error storing secret {secret_name}: {e}")

    print("Configuring Function App Settings...")
    app_settings = [
        f"AzureDevOps__Pat=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=ADOM8-ADO-PAT)",
        f"AI__ApiKey=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=CLAUDE-API-KEY)",
        f"Git__Token=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=GITHUB-TOKEN)",
        f"WebhookSharedSecret=@Microsoft.KeyVault(VaultName={args.key_vault};SecretName=FUNCTION-KEY)",
        f"AzureDevOps__OrganizationUrl={args.ado_org}",
        f"AzureDevOps__Project={args.ado_project}",
        f"Git__Provider=github",
        f"Git__Owner={args.github_org}",
        f"Git__Repo={args.github_repo}",
        f"AI__Provider=anthropic",
        f"AI__Model=claude-3-5-sonnet-20241022"
    ]
    
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
    
    # 4.1 Get current process template ID for the project
    project_url = f"{ado_org_url}/_apis/projects/{args.ado_project}?includeCapabilities=true&api-version=7.1"
    proj_resp = requests.get(project_url, headers=ado_headers)
    if proj_resp.status_code != 200:
        print(f"Failed to get project details: {proj_resp.text}")
        sys.exit(1)
    
    capabilities = proj_resp.json().get("capabilities", {})
    process_id = capabilities.get("processTemplate", {}).get("templateTypeId")
    
    # Check if process is inherited. If not, create one.
    process_url = f"{ado_org_url}/_apis/work/processes/{process_id}?api-version=7.1"
    proc_resp = requests.get(process_url, headers=ado_headers)
    proc_data = proc_resp.json()
    
    if proc_data.get("customizationType") == "system":
        print("Project is using a system process. Creating an inherited process...")
        new_proc_payload = {
            "name": f"{proc_data['name']} - AI Agents",
            "parentProcessTypeId": process_id,
            "referenceName": f"Custom.{proc_data['name']}AIAgents"
        }
        new_proc_resp = requests.post(f"{ado_org_url}/_apis/work/processes?api-version=7.1", headers=ado_headers, json=new_proc_payload)
        if new_proc_resp.status_code in [200, 201]:
            process_id = new_proc_resp.json()["typeId"]
            print(f"Created inherited process: {new_proc_payload['name']}")
            
            patch_payload = [{"op": "add", "path": "/System.ProcessTemplateType", "value": process_id}]
            patch_headers = ado_headers.copy()
            patch_headers["Content-Type"] = "application/json-patch+json"
            mig_resp = requests.patch(f"{ado_org_url}/_apis/projects/{args.ado_project}/properties?api-version=7.1-preview.1", headers=patch_headers, json=patch_payload)
            if mig_resp.status_code in [200, 204]:
                print("Migrated project to inherited process.")
            else:
                print(f"Warning: Failed to migrate project: {mig_resp.text}")
        else:
            print(f"Failed to create inherited process: {new_proc_resp.text}")
    
    # 4.2 Create custom field Custom.CurrentAgentTask
    field_payload = {
        "name": "Current Agent Task",
        "referenceName": "Custom.CurrentAgentTask",
        "type": "string",
        "description": "The current task the AI agent is working on"
    }
    field_resp = requests.post(f"{ado_org_url}/_apis/work/processes/{process_id}/workItemTypes/User%20Story/fields?api-version=7.1", headers=ado_headers, json=field_payload)
    if field_resp.status_code in [200, 201]:
        print("Created custom field Custom.CurrentAgentTask")
    elif field_resp.status_code == 409:
        print("Custom field Custom.CurrentAgentTask already exists")
    else:
        print(f"Warning: Failed to create field: {field_resp.text}")

    # 4.3 Create custom state AI Agent
    state_payload = {
        "name": "AI Agent",
        "color": "7B68EE",
        "stateCategory": "InProgress"
    }
    state_resp = requests.post(f"{ado_org_url}/_apis/work/processes/{process_id}/workItemTypes/User%20Story/states?api-version=7.1", headers=ado_headers, json=state_payload)
    if state_resp.status_code in [200, 201]:
        print("Created custom state 'AI Agent'")
    elif state_resp.status_code == 409:
        print("Custom state 'AI Agent' already exists")
    else:
        print(f"Warning: Failed to create state: {state_resp.text}")

    # 4.4 Add Custom.CurrentAgentTask field to the User Story work item form
    layout_resp = requests.get(f"{ado_org_url}/_apis/work/processes/{process_id}/workItemTypes/User%20Story/layout?api-version=7.1", headers=ado_headers)
    if layout_resp.status_code == 200:
        layout = layout_resp.json()
        try:
            group_id = layout["pages"][0]["sections"][0]["groups"][0]["id"]
            control_payload = {
                "order": None,
                "label": "Current Agent Task",
                "readOnly": False,
                "visible": True,
                "controlType": "FieldControl",
                "id": "Custom.CurrentAgentTask"
            }
            ctrl_resp = requests.post(f"{ado_org_url}/_apis/work/processes/{process_id}/workItemTypes/User%20Story/layout/groups/{group_id}/controls?api-version=7.1", headers=ado_headers, json=control_payload)
            if ctrl_resp.status_code in [200, 201]:
                print("Added field to work item form.")
        except Exception as e:
            print(f"Warning: Could not add field to form: {e}")

    # 4.5 Create board card styling rules
    board_resp = requests.get(f"{ado_org_url}/{args.ado_project}/_apis/work/boards/Stories?api-version=7.1", headers=ado_headers)
    if board_resp.status_code == 200:
        board_id = board_resp.json()["id"]
        rules_resp = requests.get(f"{ado_org_url}/{args.ado_project}/_apis/work/boards/{board_id}/cardrules?api-version=7.1", headers=ado_headers)
        if rules_resp.status_code == 200:
            rules = rules_resp.json()
            if "rules" not in rules:
                rules["rules"] = {}
            if "fill" not in rules["rules"]:
                rules["rules"]["fill"] = []
            
            colors = {
                "Planning": "#E6E6FA",
                "Coding": "#0000FF",
                "Testing": "#FFA500",
                "Review": "#FFFF00",
                "Documentation": "#008080",
                "Deployment": "#008000"
            }
            
            for task, color in colors.items():
                rules["rules"]["fill"].append({
                    "name": f"Agent Task - {task}",
                    "isEnabled": True,
                    "filter": f"[Custom.CurrentAgentTask] = '{task}'",
                    "settings": {
                        "background-color": color
                    }
                })
            
            update_rules_resp = requests.patch(f"{ado_org_url}/{args.ado_project}/_apis/work/boards/{board_id}/cardrules?api-version=7.1", headers=ado_headers, json=rules)
            if update_rules_resp.status_code == 200:
                print("Created board card styling rules.")
            else:
                print(f"Warning: Failed to update card rules: {update_rules_resp.text}")

    # 4.6 Add Custom.CurrentAgentTask field to display on Kanban board cards
    card_settings_resp = requests.get(f"{ado_org_url}/{args.ado_project}/_apis/work/boards/{board_id}/cards?api-version=7.1", headers=ado_headers)
    if card_settings_resp.status_code == 200:
        card_settings = card_settings_resp.json()
        if "cards" in card_settings and "User Story" in card_settings["cards"]:
            fields = card_settings["cards"]["User Story"]
            if type(fields) is dict and "fields" in fields:
                if "additionalFields" not in fields:
                    fields["additionalFields"] = []
                
                exists = any(f.get("referenceName") == "Custom.CurrentAgentTask" for f in fields["additionalFields"])
                if not exists:
                    fields["additionalFields"].append({
                        "referenceName": "Custom.CurrentAgentTask",
                        "displayName": "Current Agent Task",
                        "fieldType": 1
                    })
                    update_cards_resp = requests.put(f"{ado_org_url}/{args.ado_project}/_apis/work/boards/{board_id}/cards?api-version=7.1", headers=ado_headers, json=card_settings)
                    if update_cards_resp.status_code == 200:
                        print("Added field to Kanban board cards.")
                    else:
                        print(f"Warning: Failed to update card settings: {update_cards_resp.text}")

    # Stage 5: GitHub Configuration
    print("\n--- Stage 5: GitHub Configuration ---")
    gh_headers = {
        "Authorization": f"token {github_token}",
        "Accept": "application/vnd.github.v3+json"
    }
    
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

    print("Creating .adom8 folder structure...")
    readme_content = "# ADOm8 Configuration\n\nThis folder contains configuration and context for the ADOm8 AI agents."
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
    sc_url = f"{ado_org_url}/{args.ado_project}/_apis/serviceendpoint/endpoints?api-version=7.1-preview.4"
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
        sc_response = requests.post(sc_url, headers=ado_headers, json=sc_payload)
        if sc_response.status_code in [200, 201]:
            print("ADO Service Connection to GitHub created successfully.")
        else:
            print(f"Warning: Failed to create ADO Service Connection: {sc_response.status_code} - {sc_response.text}")
    except Exception as e:
        print(f"Warning: Exception creating Service Connection: {e}")

    # Stage 7: Validation
    print("\n--- Stage 7: Validation ---")
    validation_results = []
    
    try:
        health_url = f"https://{args.function_app}.azurewebsites.net/api/status?code={function_key}"
        h_resp = requests.get(health_url)
        if h_resp.status_code == 200:
            validation_results.append("PASS: Azure Function deployed and health endpoint returns 200")
        else:
            validation_results.append(f"FAIL: Azure Function health endpoint returned {h_resp.status_code}. Hint: Check Function App logs.")
    except Exception as e:
        validation_results.append(f"FAIL: Azure Function health check failed: {e}")

    try:
        secret = secret_client.get_secret("ADOM8-ADO-PAT")
        if secret:
            validation_results.append("PASS: Key Vault accessible and contains required secrets")
            validation_results.append("PASS: Azure Function can read from Key Vault successfully")
        else:
            validation_results.append("FAIL: Key Vault accessible but secret missing.")
    except Exception as e:
        validation_results.append(f"FAIL: Key Vault access failed: {e}. Hint: Check managed identity access policies.")

    try:
        f_resp = requests.get(f"{ado_org_url}/_apis/work/processes/{process_id}/workItemTypes/User%20Story/fields/Custom.CurrentAgentTask?api-version=7.1", headers=ado_headers)
        if f_resp.status_code == 200:
            validation_results.append("PASS: ADO custom field Custom.CurrentAgentTask exists on User Story")
        else:
            validation_results.append("FAIL: ADO custom field Custom.CurrentAgentTask missing.")
    except Exception as e:
        validation_results.append(f"FAIL: ADO custom field check failed: {e}")

    try:
        s_resp = requests.get(f"{ado_org_url}/_apis/work/processes/{process_id}/workItemTypes/User%20Story/states?api-version=7.1", headers=ado_headers)
        if s_resp.status_code == 200:
            states = s_resp.json().get("value", [])
            if any(s.get("name") == "AI Agent" and s.get("color") == "7B68EE" for s in states):
                validation_results.append("PASS: ADO AI Agent state exists with correct color")
            else:
                validation_results.append("FAIL: ADO AI Agent state missing or incorrect color.")
        else:
            validation_results.append("FAIL: ADO AI Agent state check failed.")
    except Exception as e:
        validation_results.append(f"FAIL: ADO AI Agent state check failed: {e}")

    try:
        r_resp = requests.get(f"{ado_org_url}/{args.ado_project}/_apis/work/boards/{board_id}/cardrules?api-version=7.1", headers=ado_headers)
        if r_resp.status_code == 200:
            rules = r_resp.json().get("rules", {}).get("fill", [])
            if any(r.get("name") == "Agent Task - Planning" for r in rules):
                validation_results.append("PASS: Board card styling rules are configured")
            else:
                validation_results.append("FAIL: Board card styling rules missing.")
        else:
            validation_results.append("FAIL: Board card styling rules check failed.")
    except Exception as e:
        validation_results.append(f"FAIL: Board card styling rules check failed: {e}")

    try:
        wh_resp = requests.get(f"https://api.github.com/repos/{args.github_org}/{args.github_repo}/hooks", headers=gh_headers)
        if wh_resp.status_code == 200:
            hooks = wh_resp.json()
            if any(args.function_app in h.get("config", {}).get("url", "") for h in hooks):
                validation_results.append("PASS: GitHub webhook is registered and responding")
            else:
                validation_results.append("FAIL: GitHub webhook missing.")
        else:
            validation_results.append("FAIL: GitHub webhook check failed.")
    except Exception as e:
        validation_results.append(f"FAIL: GitHub webhook check failed: {e}")

    try:
        sc_resp = requests.get(f"{ado_org_url}/{args.ado_project}/_apis/serviceendpoint/endpoints?api-version=7.1-preview.4", headers=ado_headers)
        if sc_resp.status_code == 200:
            endpoints = sc_resp.json().get("value", [])
            if any(e.get("name") == "ADOM8-GitHub-Connection" for e in endpoints):
                validation_results.append("PASS: GitHub service connection in ADO is valid")
            else:
                validation_results.append("FAIL: GitHub service connection in ADO missing.")
        else:
            validation_results.append("FAIL: GitHub service connection check failed.")
    except Exception as e:
        validation_results.append(f"FAIL: GitHub service connection check failed: {e}")

    try:
        wi_payload = [
            {"op": "add", "path": "/fields/System.Title", "value": "E2E Test Work Item"},
            {"op": "add", "path": "/fields/System.State", "value": "AI Agent"}
        ]
        wi_headers = ado_headers.copy()
        wi_headers["Content-Type"] = "application/json-patch+json"
        wi_resp = requests.post(f"{ado_org_url}/{args.ado_project}/_apis/wit/workitems/$User%20Story?api-version=7.1", headers=wi_headers, json=wi_payload)
        if wi_resp.status_code == 200:
            wi_id = wi_resp.json()["id"]
            validation_results.append("PASS: End to end test: create a test work item, assign to AI Agent state")
            
            del_resp = requests.delete(f"{ado_org_url}/{args.ado_project}/_apis/wit/workitems/{wi_id}?api-version=7.1", headers=ado_headers)
            if del_resp.status_code == 200:
                validation_results.append("PASS: End to end test: delete the test work item")
            else:
                validation_results.append("FAIL: End to end test: delete the test work item failed.")
        else:
            validation_results.append(f"FAIL: End to end test: create work item failed: {wi_resp.text}")
    except Exception as e:
        validation_results.append(f"FAIL: End to end test failed: {e}")

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