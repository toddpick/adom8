# Codebase Setup Guide

> **One-time setup** to prepare your repository for ADO-AI Agents.

## Why This Step Exists

ADO-AI Agents need context about your codebase to make good changes — coding standards, architecture patterns, file organization, testing conventions. This context lives in a `.agent/` folder in your repo.

## Quick Start (2 minutes)

### Option A: Use the Setup Script (Recommended)

**PowerShell (Windows):**
```powershell
cd your-repo
.\setup\initialize-codebase.ps1
```

**Bash (macOS/Linux):**
```bash
cd your-repo
chmod +x setup/initialize-codebase.sh
./setup/initialize-codebase.sh
```

The script will:
1. Scan your codebase structure
2. Sample key source files
3. Detect your tech stack
4. Generate `.agent-setup-prompt.md`

Then:
1. Open `.agent-setup-prompt.md`
2. Copy the entire file contents
3. Paste into your AI assistant (GitHub Copilot Chat, Claude, ChatGPT)
4. The AI generates 8 documentation files
5. Save each to `.agent/` in your repo root
6. Commit and push

### Option B: Ask Your AI Assistant Directly

If you're using GitHub Copilot in VS Code, you can skip the script entirely:

1. Open Copilot Chat
2. Say:

> Scan this entire codebase and generate a `.agent/` folder with these documentation files:
> CONTEXT_INDEX.md, TECH_STACK.md, ARCHITECTURE.md, CODING_STANDARDS.md,
> COMMON_PATTERNS.md, TESTING_STRATEGY.md, DEPLOYMENT.md, API_REFERENCE.md.
>
> Each file should contain specific details from THIS codebase — actual file paths,
> class names, patterns, and code examples. Use mermaid diagrams for architecture.
> These docs will be used by AI coding agents as context when making changes.

3. Review the generated files
4. Commit and push

### Option C: Write Them Manually

If you prefer full control, create `.agent/` with these files:

| File | Purpose | Key Content |
|------|---------|-------------|
| `CONTEXT_INDEX.md` | Master overview | Project structure, quick reference, key patterns |
| `TECH_STACK.md` | Technology inventory | Languages, frameworks, versions, dependencies |
| `ARCHITECTURE.md` | System design | Component diagram (mermaid), data flow, design decisions |
| `CODING_STANDARDS.md` | Code conventions | Naming, file org, error handling, logging patterns |
| `COMMON_PATTERNS.md` | How-to guides | Add a feature, endpoint, test, migration |
| `TESTING_STRATEGY.md` | Test approach | Framework, patterns, naming, running tests |
| `DEPLOYMENT.md` | Deploy process | Build, CI/CD, infrastructure, configuration |
| `API_REFERENCE.md` | API docs | Endpoints, interfaces, request/response formats |

## What the AI Agents Use

The `CodebaseContextLoader` service loads these docs selectively based on what the agent is working on:

- **Always loaded:** CONTEXT_INDEX.md, CODING_STANDARDS.md, TECH_STACK.md
- **By keyword match:** "database" → DATABASE_SCHEMA.md, "api" → API_REFERENCE.md, etc.
- **Feature docs:** `.agent/FEATURES/*.md` matched by word overlap with the work item

## Keeping Docs Updated

These docs don't need frequent updates. Update them when:
- Major architectural changes (new service layer, database migration)
- New technology added (switching frameworks, adding a queue)
- Significant pattern changes (new error handling approach)

You can re-run the setup script anytime to regenerate the prompt and refresh the docs.

## Troubleshooting

**Q: The AI generated docs that are too generic.**
A: Make sure the prompt includes actual code samples. The setup script does this automatically.

**Q: My codebase is very large (100k+ lines).**
A: The script samples the most important files (entry points, services, models). You can increase `MaxSampleFiles` to 25-30, but beyond that the prompt may exceed AI context limits.

**Q: Do I need to generate all 8 files?**
A: CONTEXT_INDEX.md and CODING_STANDARDS.md are the most important. The others improve agent quality but aren't required.
