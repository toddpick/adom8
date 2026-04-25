# ADOm8 Managed Onboarding (Control Plane)

This folder scaffolds a **parallel managed onboarding path** that provisions project-isolated ADOm8 deployments from a shared control plane.

## What is included

- Durable Function registration API + status endpoint
- Provisioning orchestrator and activity contracts
- Model and service contracts for onboarding lifecycle
- Bicep templates (`infra/shared.bicep`, `infra/project.bicep`)
- Starter Next.js onboarding UI pages/components
- Shared deployment pipeline (`.ado/pipelines/deploy-shared.yml`)

## Design goals

- No modifications to existing ADOm8 orchestration path in this repository
- Zero-clone GitHub operations (REST API only)
- Managed identity-first Azure SDK usage
- Idempotent provisioning stages

## Notes

This is an implementation scaffold intended to be iterated in a dedicated `toddpick/adom8-onboarding` repo.
