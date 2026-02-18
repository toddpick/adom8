# Developer Scripts

Helper scripts for working with AI agents and maintaining code consistency.

## ai-with-context.sh

Wrapper for AI coding CLIs (Claude Code, OpenAI Codex) that automatically includes `.agent/` documentation context in prompts.

### Usage

```bash
# Auto-detect available CLI
./scripts/ai-with-context.sh "task description"

# Force a specific CLI
./scripts/ai-with-context.sh --tool claude "task description"
./scripts/ai-with-context.sh --tool codex "task description"
```

### Examples

```bash
# Add a new feature
./scripts/ai-with-context.sh "add OAuth2 support to authentication"

# Refactor existing code
./scripts/ai-with-context.sh "refactor StoryContext to support multi-region storage"

# Add tests
./scripts/ai-with-context.sh "add integration tests for DeploymentAgentService"

# Fix a bug (using a specific CLI)
./scripts/ai-with-context.sh --tool codex "fix race condition in concurrent state file access"
```

### What It Does

1. Validates `.agent/` folder exists
2. Auto-detects installed AI CLIs (Claude Code and/or OpenAI Codex)
3. Loads context files automatically:
   - `.agent/CONTEXT_INDEX.md` — master overview (always loaded)
   - `.agent/CODING_STANDARDS.md` — conventions and patterns (always loaded)
   - `.agent/ARCHITECTURE.md` — system design (if present)
   - `.agent/COMMON_PATTERNS.md` — how-to patterns (if present)
   - `.agent/FEATURES/*.md` — all feature documentation (if present)
4. Builds a structured prompt with your task + loaded context
5. Calls the detected (or specified) CLI with the complete prompt

### Prerequisites

- **At least one AI CLI** installed and in PATH:
  ```bash
  # Claude Code CLI
  npm install -g @anthropic-ai/claude-code

  # OpenAI Codex CLI
  npm install -g @openai/codex
  ```
- **`.agent/` folder populated** — run CodebaseDocumentationAgent first via the dashboard, or create manually
- Run from the **repository root** (where `.agent/` folder is located)

### CLI Selection

| Scenario | Behavior |
|----------|----------|
| `--tool claude` | Uses Claude Code CLI (error if not installed) |
| `--tool codex` | Uses OpenAI Codex CLI (error if not installed) |
| No `--tool` flag | Auto-detects: prefers Claude Code if both are installed |
| Neither installed | Prints prompt for copy/paste into any AI tool |

### Fallback Mode

If no AI CLI is installed, the script prints the generated prompt so you can copy/paste it into any AI tool (Cursor, GitHub Copilot Chat, ChatGPT, etc.).

### Why Use This

| Without Context | With Context |
|----------------|--------------|
| Generic code patterns | Code matching YOUR codebase conventions |
| Random naming | Consistent naming (`I{Feature}`, `{Name}AgentService`) |
| Mixed DI approaches | Constructor injection, `Program.cs` registration |
| Mixed test frameworks | xUnit + Moq, `Method_Scenario_Result` naming |
| Missing cancellation tokens | `CancellationToken` on all async methods |

**Result:** Consistent code whether written by AI agents, humans, or humans with AI tools.

## Adding New Scripts

When adding developer helper scripts:

1. Place them in the `scripts/` directory
2. Include a shebang line (`#!/bin/bash`)
3. Add `set -euo pipefail` for safety
4. Include usage instructions when called without arguments
5. Validate prerequisites before executing
6. Document the script in this README
7. Make executable: `chmod +x scripts/your-script.sh`

## bootstrap.ps1

One-command infrastructure + app setup + deployment automation for Windows PowerShell.

### What It Automates

1. Writes `infrastructure/terraform.tfvars` from your config
2. Runs Terraform (`init` + `apply`)
3. Configures Function App app settings (AI, ADO, Git, optional Copilot)
4. Publishes Azure Functions
5. Updates dashboard API URL to your deployed Function App
6. Deploys Static Web App dashboard

### Usage

```powershell
# 1) Create editable config from template
.\scripts\bootstrap.ps1 -InitConfig

# 2) Edit scripts\bootstrap.config.json with your values (PATs, API keys, names)

# 3) Run full bootstrap
.\scripts\bootstrap.ps1 -ConfigPath .\scripts\bootstrap.config.json
```

### Optional Flags

```powershell
# Reuse existing infrastructure outputs only
.\scripts\bootstrap.ps1 -SkipTerraform

# Skip Functions publish
.\scripts\bootstrap.ps1 -SkipFunctions

# Skip dashboard deploy
.\scripts\bootstrap.ps1 -SkipDashboard
```

### Required Manual Inputs

- Azure DevOps PAT
- Git PAT (GitHub or ADO Repos)
- AI provider API key

Those values go into `scripts/bootstrap.config.json`; everything else is automated.
