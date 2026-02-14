#!/bin/bash
# =============================================================================
# ai-with-context.sh
# Helper script to run AI coding CLIs with .agent/ documentation context.
#
# Supports: Claude Code CLI, OpenAI Codex CLI, or fallback prompt output.
# Automatically loads codebase documentation so AI tools generate code
# that matches your team's established patterns and conventions.
#
# Usage:
#   ./scripts/ai-with-context.sh "task description"
#   ./scripts/ai-with-context.sh --tool claude "task description"
#   ./scripts/ai-with-context.sh --tool codex "task description"
#
# Example:
#   ./scripts/ai-with-context.sh "add OAuth2 authentication to the API"
#
# Prerequisites:
#   - Claude Code CLI and/or OpenAI Codex CLI installed and in PATH
#   - .agent/ folder populated (run CodebaseDocumentationAgent first)
# =============================================================================

set -euo pipefail

# --- Parse arguments ---

TOOL=""
TASK=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tool|-t)
            TOOL="${2:-}"
            shift 2
            ;;
        --help|-h)
            echo "Usage: ./scripts/ai-with-context.sh [--tool claude|codex] 'task description'"
            echo ""
            echo "Options:"
            echo "  --tool, -t    Force a specific CLI: 'claude' or 'codex'"
            echo "                If omitted, auto-detects which CLI is installed"
            echo "  --help, -h    Show this help message"
            echo ""
            echo "Examples:"
            echo "  ./scripts/ai-with-context.sh 'add OAuth2 authentication'"
            echo "  ./scripts/ai-with-context.sh --tool codex 'refactor StoryContext'"
            echo "  ./scripts/ai-with-context.sh --tool claude 'add rate limiting'"
            echo ""
            echo "Supported CLIs:"
            echo "  Claude Code:  npm install -g @anthropic-ai/claude-code"
            echo "  OpenAI Codex: npm install -g @openai/codex"
            echo ""
            echo "If neither CLI is installed, the script prints the prompt so you"
            echo "can copy/paste it into Cursor, GitHub Copilot Chat, or any AI tool."
            exit 0
            ;;
        *)
            TASK="$1"
            shift
            ;;
    esac
done

if [ -z "$TASK" ]; then
    echo "Usage: ./scripts/ai-with-context.sh [--tool claude|codex] 'task description'"
    echo ""
    echo "Examples:"
    echo "  ./scripts/ai-with-context.sh 'add OAuth2 authentication'"
    echo "  ./scripts/ai-with-context.sh --tool codex 'refactor StoryContext'"
    echo "  ./scripts/ai-with-context.sh --tool claude 'add rate limiting'"
    echo ""
    echo "This script automatically includes .agent/ documentation context"
    echo "so AI coding tools generate code matching your codebase patterns."
    echo ""
    echo "Run with --help for more options."
    exit 1
fi

# --- Prerequisite checks ---

# Check for .agent/ folder
if [ ! -d ".agent" ]; then
    echo "Error: .agent/ folder not found in current directory."
    echo ""
    echo "The .agent/ folder contains AI-optimized documentation about your codebase."
    echo "To generate it:"
    echo "  1. Open the AI Agents dashboard"
    echo "  2. Trigger the CodebaseDocumentationAgent"
    echo "  3. Wait for it to complete and push .agent/ files"
    echo "  4. Pull the latest changes: git pull"
    echo ""
    echo "Alternatively, create the folder manually:"
    echo "  mkdir -p .agent"
    echo "  # Add CONTEXT_INDEX.md, CODING_STANDARDS.md, etc."
    exit 1
fi

# --- Detect available CLI ---

CLAUDE_AVAILABLE=false
CODEX_AVAILABLE=false

if command -v claude &> /dev/null; then
    CLAUDE_AVAILABLE=true
fi

if command -v codex &> /dev/null; then
    CODEX_AVAILABLE=true
fi

# Determine which tool to use
SELECTED_TOOL=""
if [ -n "$TOOL" ]; then
    # User explicitly requested a tool
    case "$TOOL" in
        claude)
            if [ "$CLAUDE_AVAILABLE" = false ]; then
                echo "Warning: 'claude' CLI not found in PATH."
                echo "Install: npm install -g @anthropic-ai/claude-code"
                echo ""
                echo "Building prompt anyway (you can copy/paste it)..."
                echo ""
            else
                SELECTED_TOOL="claude"
            fi
            ;;
        codex)
            if [ "$CODEX_AVAILABLE" = false ]; then
                echo "Warning: 'codex' CLI not found in PATH."
                echo "Install: npm install -g @openai/codex"
                echo ""
                echo "Building prompt anyway (you can copy/paste it)..."
                echo ""
            else
                SELECTED_TOOL="codex"
            fi
            ;;
        *)
            echo "Error: Unknown tool '$TOOL'. Supported: claude, codex"
            exit 1
            ;;
    esac
else
    # Auto-detect: prefer whichever is available (claude first, then codex)
    if [ "$CLAUDE_AVAILABLE" = true ]; then
        SELECTED_TOOL="claude"
    elif [ "$CODEX_AVAILABLE" = true ]; then
        SELECTED_TOOL="codex"
    else
        echo "No AI CLI detected. Install one of:"
        echo "  Claude Code:  npm install -g @anthropic-ai/claude-code"
        echo "  OpenAI Codex: npm install -g @openai/codex"
        echo ""
        echo "Building prompt anyway (you can copy/paste it into any AI tool)..."
        echo ""
    fi
fi

# --- Load context files ---

echo "Loading context from .agent/ folder..."

CONTEXT=""
FILES_LOADED=0

# Always load CONTEXT_INDEX.md (master overview)
if [ -f ".agent/CONTEXT_INDEX.md" ]; then
    CONTEXT+="=== CONTEXT_INDEX.md ===
"
    CONTEXT+="$(cat .agent/CONTEXT_INDEX.md)"
    CONTEXT+="

"
    FILES_LOADED=$((FILES_LOADED + 1))
    echo "  ✓ Loaded CONTEXT_INDEX.md"
fi

# Always load CODING_STANDARDS.md (conventions)
if [ -f ".agent/CODING_STANDARDS.md" ]; then
    CONTEXT+="=== CODING_STANDARDS.md ===
"
    CONTEXT+="$(cat .agent/CODING_STANDARDS.md)"
    CONTEXT+="

"
    FILES_LOADED=$((FILES_LOADED + 1))
    echo "  ✓ Loaded CODING_STANDARDS.md"
fi

# Load ARCHITECTURE.md if present
if [ -f ".agent/ARCHITECTURE.md" ]; then
    CONTEXT+="=== ARCHITECTURE.md ===
"
    CONTEXT+="$(cat .agent/ARCHITECTURE.md)"
    CONTEXT+="

"
    FILES_LOADED=$((FILES_LOADED + 1))
    echo "  ✓ Loaded ARCHITECTURE.md"
fi

# Load COMMON_PATTERNS.md if present
if [ -f ".agent/COMMON_PATTERNS.md" ]; then
    CONTEXT+="=== COMMON_PATTERNS.md ===
"
    CONTEXT+="$(cat .agent/COMMON_PATTERNS.md)"
    CONTEXT+="

"
    FILES_LOADED=$((FILES_LOADED + 1))
    echo "  ✓ Loaded COMMON_PATTERNS.md"
fi

# Load all feature files
if [ -d ".agent/FEATURES" ]; then
    for feature_file in .agent/FEATURES/*.md; do
        if [ -f "$feature_file" ]; then
            FEATURE_NAME=$(basename "$feature_file")
            CONTEXT+="=== FEATURES/$FEATURE_NAME ===
"
            CONTEXT+="$(cat "$feature_file")"
            CONTEXT+="

"
            FILES_LOADED=$((FILES_LOADED + 1))
            echo "  ✓ Loaded FEATURES/$FEATURE_NAME"
        fi
    done
fi

if [ "$FILES_LOADED" -eq 0 ]; then
    echo ""
    echo "Warning: No documentation files found in .agent/ folder."
    echo "Run CodebaseDocumentationAgent to generate documentation."
    echo "Proceeding without context..."
    echo ""
fi

echo ""
echo "Loaded $FILES_LOADED context files."
echo ""

# --- Build prompt ---

PROMPT="Task: $TASK

Context from codebase (.agent/ documentation):

$CONTEXT

Requirements:
- Follow all patterns and conventions documented above
- Match existing code style and architecture
- Use established interfaces and patterns (I{Feature} interface pattern)
- Use constructor dependency injection (register in Program.cs)
- Add XML comments on all public methods and classes
- Document decisions in .ado/stories/US-{workItemId}/DECISIONS.md
- Add comprehensive tests (80%+ coverage) using xUnit + Moq
- Use structured logging with ILogger<T>
- Follow naming: PascalCase for types, _camelCase for fields, Method_Scenario_Result for tests
- Include CancellationToken on all async methods
- Use sealed classes where inheritance isn't needed
- Link code to user story in comments where applicable

Proceed with implementation following the documented patterns."

# --- Execute ---

case "$SELECTED_TOOL" in
    claude)
        echo "Calling Claude Code CLI with context..."
        echo "---"
        echo ""
        claude "$PROMPT"
        ;;
    codex)
        echo "Calling OpenAI Codex CLI with context..."
        echo "---"
        echo ""
        codex "$PROMPT"
        ;;
    *)
        echo "===== GENERATED PROMPT (copy/paste into your AI tool) ====="
        echo ""
        echo "$PROMPT"
        echo ""
        echo "===== END PROMPT ====="
        echo ""
        echo "Paste this prompt into: Cursor, GitHub Copilot Chat, ChatGPT, Claude, or any AI tool."
        exit 0
        ;;
esac
