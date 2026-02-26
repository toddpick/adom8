# Feature: Codebase Intelligence

## Overview

Codebase Intelligence is the system that generates and loads AI-optimized documentation for the repository. It consists of two parts:

1. **`CodebaseDocumentationAgentService`** — a 7th agent that analyzes the entire codebase, historical stories, and git commits to generate the `.agent/` folder documentation.
2. **`CodebaseContextLoader`** — used by all other agents to load relevant `.agent/` documentation into their AI prompts, ensuring architecture-aware, pattern-consistent code generation.

## Key Files

| File | Purpose |
|------|---------|
| `src/AIAgents.Functions/Agents/CodebaseDocumentationAgentService.cs` | Documentation generation agent |
| `src/AIAgents.Core/Services/CodebaseContextLoader.cs` | Context loading for agent prompts |
| `src/AIAgents.Core/Interfaces/ICodebaseContextProvider.cs` | Context provider contract |
| `src/AIAgents.Core/Models/CodebaseAnalysis.cs` | Analysis result models |
| `src/AIAgents.Core/Configuration/CodebaseDocumentationOptions.cs` | Config: OutputFolder, MaxFilesToAnalyze, etc. |
| `src/AIAgents.Functions/Functions/CodebaseIntelligence.cs` | HTTP trigger for dashboard-initiated scans |
| `.agent/` | Generated documentation folder (this folder) |
| `.agent/FEATURES/` | Per-feature documentation files (this subfolder) |

## Architecture / Data Flow

```mermaid
flowchart TD
    subgraph "Documentation Generation"
        TRIGGER[Dashboard Button<br/>or WI State Change] -->|HTTP POST /api/codebase-intelligence| CI[CodebaseIntelligence Function]
        CI -->|Enqueue AgentTask{WI=0, Type=CodebaseDocumentation}| Q[Azure Storage Queue]
        Q --> ATD[AgentTaskDispatcher]
        ATD --> CDAS[CodebaseDocumentationAgentService]
        CDAS -->|git clone main| REPO[Repository]
        CDAS -->|Read 30-50 source files| FILES[Source Files]
        CDAS -->|Read .ado/stories/*| STORIES[Story History]
        CDAS -->|git log| HISTORY[Git History]
        CDAS -->|CompleteAsync| AI[AI Provider]
        AI -->|Generated docs| CDAS
        CDAS -->|Write .agent/*.md| REPO
        CDAS -->|git commit + push main| REMOTE[Remote Repo]
    end

    subgraph "Context Loading (per agent run)"
        AGENT[Agent Service] -->|LoadRelevantContextAsync| CCL[CodebaseContextLoader]
        CCL -->|Read CONTEXT_INDEX.md| CTX[Always loaded]
        CCL -->|Read CODING_STANDARDS.md| CTX
        CCL -->|Read TECH_STACK.md| CTX
        CCL -->|Keyword match| SUP[Supplementary files<br/>API_REFERENCE, DEPLOYMENT, etc.]
        CCL -->|Feature keyword match| FEAT[FEATURES/*.md]
        CCL -->|Return combined context| AGENT
        AGENT -->|Include in AI prompt| AI2[AI Provider]
    end
```

## Context Loading Logic

`CodebaseContextLoader.LoadRelevantContextAsync` uses a layered strategy to avoid loading all documentation (token limits):

1. **Always loaded** (core files): `CONTEXT_INDEX.md`, `CODING_STANDARDS.md`, `TECH_STACK.md`

2. **Keyword-triggered** (supplementary files):

| Keywords in Work Item | File Loaded |
|---|---|
| database, schema, table, migration, sql | `DATABASE_SCHEMA.md` |
| api, endpoint, rest, controller, route | `API_REFERENCE.md` |
| deploy, pipeline, ci/cd, terraform | `DEPLOYMENT.md` |
| test, unit test, mock, coverage | `TESTING_STRATEGY.md` |
| pattern, convention, how to, template | `COMMON_PATTERNS.md` |
| architect, design, diagram, flow | `ARCHITECTURE.md` |

3. **Feature files** (`FEATURES/*.md`): Matched by filename words. A work item mentioning "pipeline" loads `FEATURES/agent-pipeline.md`; "dashboard" loads `FEATURES/dashboard.md`.

## CodebaseDocumentationAgentService Workflow

The agent runs on the `main` branch (not a feature branch) and commits docs directly:

1. Clone/open repo on `main` branch
2. Check for existing `metadata.json` (incremental analysis)
3. Build file tree (excluding build artifacts: bin, obj, node_modules, etc.)
4. Sample 30–50 key source files (entry points, services, agents, models, tests)
5. Read historical stories from `.ado/stories/*/`
6. Get recent git commit history
7. Call AI to generate each documentation file
8. Write all files to `.agent/` folder
9. Commit and push to `main`
10. Update `metadata.json` with analysis statistics

## Configuration

Bound under `CodebaseDocumentation` section:

```json
{
  "CodebaseDocumentation": {
    "OutputFolder": ".agent",
    "MaxFilesToAnalyze": 50,
    "MaxStoriesInContext": 10,
    "MaxCommitsInContext": 30,
    "ExcludePatterns": ["bin/", "obj/", "node_modules/", ".git/"]
  }
}
```

## Triggering a Documentation Scan

**Via Dashboard**: Click "Scan Codebase" button → calls `POST /api/codebase-intelligence` → enqueues `AgentTask{WorkItemId=0, AgentType=CodebaseDocumentation}`.

**Via ADO**: Move a work item to a state that maps to `CodebaseDocumentation` agent type (if configured).

**Incremental mode**: Send description containing "incremental" to only update docs changed since the last scan.

## How to Add a New Feature Document

1. Create a new file in `.agent/FEATURES/` (e.g., `my-feature.md`)
2. Follow this structure:
   - **Overview**: 2–3 sentence summary
   - **Key Files**: table of relevant files
   - **Architecture / Data Flow**: Mermaid diagram
   - **Configuration**: config section (if applicable)
   - **How to Extend**: step-by-step guide
   - **Testing Approach**: how tests are structured

3. The `CodebaseContextLoader` automatically discovers and loads `FEATURES/*.md` files — no registration required.

4. Files are matched to work items by filename keywords. Choose descriptive names (e.g., `notification-system.md` loads for work items mentioning "notification").

## How to Refresh Documentation

Run the CodebaseDocumentation agent:
```
POST /api/codebase-intelligence
Body: {} (or {"workItemId": 0, "mode": "incremental"})
```

Or trigger it from the ADOm8 dashboard using the "Scan Codebase" button.

## Testing Approach

The agent itself does not have a dedicated test file currently. Context loading is tested via integration:
- Mock `IGitOperations` to return controlled file content
- Verify that `LoadRelevantContextAsync` returns the expected sections based on keyword matching
- Test that `HasCodebaseDocumentationAsync` correctly detects presence/absence of `CONTEXT_INDEX.md`
