# Production Security Hardening Guide

This guide upgrades the default setup to a production-grade security baseline.

## Why this matters

The quick-start path stores secrets in Function App settings for speed and simplicity. For production, move all sensitive values to Azure Key Vault and use managed identity + Key Vault references.

## Recommended baseline (15-30 min)

1. Use a **System-Assigned Managed Identity** on the Function App.
2. Store all secrets in **Azure Key Vault**.
3. Replace sensitive app settings values with **Key Vault references**.
4. Restrict PAT scopes to minimum required permissions.
5. Protect dashboard access (SWA auth / Entra ID).
6. Add alerting for high-risk endpoints (`reset`, `emergency-stop`, `provision-ado`).

> **Automation option:** `scripts/bootstrap.ps1` supports end-to-end Key Vault setup when `keyVault.enabled=true` in your bootstrap config.

## Secrets to move into Key Vault

Move these values out of plain app settings:

- `AI__ApiKey`
- `AzureDevOps__Pat`
- `Git__Token`
- `GitHub__Token`
- `Copilot__WebhookSecret`

## Key Vault setup steps

### 1) Create Key Vault and secrets

```bash
az keyvault create \
  --name <kv-name> \
  --resource-group <rg-name> \
  --location <region>

az keyvault secret set --vault-name <kv-name> --name AI-ApiKey --value "<your-ai-key>"
az keyvault secret set --vault-name <kv-name> --name Ado-Pat --value "<your-ado-pat>"
az keyvault secret set --vault-name <kv-name> --name Git-Token --value "<your-git-token>"
az keyvault secret set --vault-name <kv-name> --name GitHub-Token --value "<your-github-token>"
az keyvault secret set --vault-name <kv-name> --name Copilot-WebhookSecret --value "<your-webhook-secret>"
```

### 2) Enable Function App managed identity

```bash
az functionapp identity assign \
  --name <function-app-name> \
  --resource-group <rg-name>
```

### 3) Grant Key Vault secret read access

Use either Key Vault RBAC (recommended) or access policy.

**RBAC example:**

```bash
principalId=$(az functionapp identity show \
  --name <function-app-name> \
  --resource-group <rg-name> \
  --query principalId -o tsv)

kvId=$(az keyvault show --name <kv-name> --query id -o tsv)

az role assignment create \
  --assignee-object-id "$principalId" \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope "$kvId"
```

### 4) Replace app settings with Key Vault references

```bash
az functionapp config appsettings set \
  --name <function-app-name> \
  --resource-group <rg-name> \
  --settings \
    "AI__ApiKey=@Microsoft.KeyVault(SecretUri=https://<kv-name>.vault.azure.net/secrets/AI-ApiKey/)" \
    "AzureDevOps__Pat=@Microsoft.KeyVault(SecretUri=https://<kv-name>.vault.azure.net/secrets/Ado-Pat/)" \
    "Git__Token=@Microsoft.KeyVault(SecretUri=https://<kv-name>.vault.azure.net/secrets/Git-Token/)" \
    "GitHub__Token=@Microsoft.KeyVault(SecretUri=https://<kv-name>.vault.azure.net/secrets/GitHub-Token/)" \
    "Copilot__WebhookSecret=@Microsoft.KeyVault(SecretUri=https://<kv-name>.vault.azure.net/secrets/Copilot-WebhookSecret/)"
```

## PAT scope hardening

Use one token per integration where practical.

- **ADO PAT:** only required scopes from setup guide
- **GitHub PAT:** repo-limited fine-grained token preferred
- Rotate every 30-90 days
- Revoke immediately on suspected exposure

## Dashboard & endpoint hardening

- Keep mutating endpoints at `AuthorizationLevel.Function` (already implemented).
- Store Function key outside screenshots/docs and rotate periodically.
- For production, gate Static Web App with Entra ID auth and restrict who can invoke controls.

## Monitoring and alerting checklist

Set alerts for:

- Failed executions > threshold (Functions)
- Poison queue depth > 0
- Excess calls to `emergency-stop`, `reset`, or `provision-ado`
- Key Vault access failures

## Incident response quick runbook

If a secret/token is leaked:

1. Rotate secret in provider (ADO/GitHub/AI).
2. Update Key Vault secret value.
3. Restart Function App.
4. Review Application Insights logs for suspicious endpoint use.
5. Document impact window and corrective actions.
