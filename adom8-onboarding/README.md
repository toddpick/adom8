# ADOm8 Managed Onboarding (Control Plane)

This folder contains the optional **portfolio onboarding control plane** for organizations that want to onboard multiple ADOm8 projects into one shared Azure subscription without forking ADOm8 for every project.

## What is included

- Durable Function registration API + status endpoint
- Provisioning orchestrator and activity contracts
- Model and service contracts for onboarding lifecycle
- Bicep templates (`infra/shared.bicep`, `infra/project.bicep`) for the shared control plane and per-project runtime resources
- Starter Next.js onboarding UI pages/components
- Shared deployment pipeline (`.ado/pipelines/deploy-shared.yml`)

## Design goals

- Keep the public "fork ADOm8 and run the onboarding pipeline" path intact
- Reuse the existing ADOm8 runtime (`src/AIAgents.*`), dashboard, and setup/provisioning behavior instead of creating a second agent engine
- Zero-clone GitHub operations (REST API only)
- Managed identity-first Azure SDK usage
- Idempotent provisioning stages

## Notes

This control plane should remain optional. It is meant for portfolio/shared-subscription deployments; single-project self-hosted users can keep using `adom8-onboarding-pipeline.yml`.

## Local checks

```bash
dotnet build adom8-onboarding/src/Functions/adom8-onboarding.csproj
az bicep build --file adom8-onboarding/infra/shared.bicep
az bicep build --file adom8-onboarding/infra/project.bicep
cd adom8-onboarding/src/web && npm ci && npm run build
```

The starter web app calls the onboarding Functions API using:

- `NEXT_PUBLIC_ONBOARDING_API_BASE_URL` - onboarding Function App base URL
- `NEXT_PUBLIC_ONBOARDING_FUNCTION_KEY` - temporary POC function key until Entra/SWA auth is added
