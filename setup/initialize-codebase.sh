#!/usr/bin/env bash
#
# ADO-AI Agents — Codebase Initializer (Bash version)
#
# Scans your codebase and generates a prompt for an AI assistant
# to create .agent/ documentation files.
#
# Usage: ./initialize-codebase.sh [repo-path] [max-sample-files]
#

set -euo pipefail

REPO_PATH="${1:-.}"
MAX_SAMPLES="${2:-15}"
OUTPUT_FILE="$REPO_PATH/.agent-setup-prompt.md"

echo ""
echo "========================================"
echo "  ADO-AI Agents - Codebase Initializer"
echo "========================================"
echo ""
echo "Scanning: $(cd "$REPO_PATH" && pwd)"
echo ""

# ─── Step 1: File Tree ───────────────────────────────────────────────

echo "[1/5] Scanning file structure..."

FILE_TREE=$(find "$REPO_PATH" -type f \
    -not -path '*/.git/*' \
    -not -path '*/node_modules/*' \
    -not -path '*/bin/*' \
    -not -path '*/obj/*' \
    -not -path '*/.vs/*' \
    -not -path '*/__pycache__/*' \
    -not -path '*/.terraform/*' \
    -not -path '*/dist/*' \
    -not -path '*/build/*' \
    -not -path '*/coverage/*' \
    -not -path '*/.agent/*' \
    -not -path '*/.ado/*' \
    -not -path '*/publish/*' \
    -not -name '*.dll' -not -name '*.exe' -not -name '*.pdb' \
    -not -name '*.png' -not -name '*.jpg' -not -name '*.gif' \
    -not -name '*.ico' -not -name '*.svg' -not -name '*.zip' \
    -not -name '*.gz' -not -name '*.lock' \
    | sed "s|^$REPO_PATH/||" \
    | sort)

FILE_COUNT=$(echo "$FILE_TREE" | wc -l | tr -d ' ')
echo "  Found $FILE_COUNT files"

# ─── Step 2: Detect Tech Stack ──────────────────────────────────────

echo "[2/5] Detecting tech stack..."

TECH=""
[ -n "$(find "$REPO_PATH" -name '*.csproj' -maxdepth 3 2>/dev/null | head -1)" ] && TECH="$TECH\n- .NET (C#)"
[ -f "$REPO_PATH/package.json" ] && TECH="$TECH\n- Node.js"
[ -f "$REPO_PATH/requirements.txt" ] || [ -f "$REPO_PATH/pyproject.toml" ] && TECH="$TECH\n- Python"
[ -f "$REPO_PATH/go.mod" ] && TECH="$TECH\n- Go"
[ -f "$REPO_PATH/Cargo.toml" ] && TECH="$TECH\n- Rust"
[ -f "$REPO_PATH/pom.xml" ] && TECH="$TECH\n- Java (Maven)"
[ -f "$REPO_PATH/Dockerfile" ] && TECH="$TECH\n- Docker"
[ -n "$(find "$REPO_PATH" -name '*.tf' -maxdepth 3 2>/dev/null | head -1)" ] && TECH="$TECH\n- Terraform"
[ -f "$REPO_PATH/tsconfig.json" ] && TECH="$TECH\n- TypeScript"

echo -e "  Detected:$TECH"

# ─── Step 3: Sample Code Files ──────────────────────────────────────

echo "[3/5] Sampling code files..."

CODE_SAMPLES=""
SAMPLE_COUNT=0

# Find code files, prioritize key files
SAMPLED_FILES=$(find "$REPO_PATH" -type f \
    \( -name '*.cs' -o -name '*.ts' -o -name '*.js' -o -name '*.py' \
       -o -name '*.go' -o -name '*.rs' -o -name '*.java' -o -name '*.rb' \) \
    -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/node_modules/*' \
    -not -path '*/.git/*' \
    | head -n "$MAX_SAMPLES")

while IFS= read -r file; do
    [ -z "$file" ] && continue
    REL_PATH=$(echo "$file" | sed "s|^$REPO_PATH/||")
    CONTENT=$(head -120 "$file" 2>/dev/null || true)
    if [ -n "$CONTENT" ]; then
        EXT="${file##*.}"
        CODE_SAMPLES="$CODE_SAMPLES
### $REL_PATH
\`\`\`$EXT
$CONTENT
\`\`\`
"
        SAMPLE_COUNT=$((SAMPLE_COUNT + 1))
    fi
done <<< "$SAMPLED_FILES"

echo "  Sampled $SAMPLE_COUNT files"

# ─── Step 4: Count Tests ────────────────────────────────────────────

echo "[4/5] Detecting tests..."

TEST_COUNT=$(find "$REPO_PATH" -type f \
    \( -name '*Test*' -o -name '*test*' -o -name '*Spec*' -o -name '*spec*' \) \
    \( -name '*.cs' -o -name '*.ts' -o -name '*.js' -o -name '*.py' -o -name '*.java' \) \
    -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/node_modules/*' \
    | wc -l | tr -d ' ')

echo "  Found $TEST_COUNT test files"

# ─── Step 5: Generate Prompt ────────────────────────────────────────

echo "[5/5] Generating AI prompt..."

cat > "$OUTPUT_FILE" << PROMPT_EOF
# Codebase Analysis — Generate .agent/ Documentation

You are analyzing a codebase to generate documentation that AI coding agents will use as context.
Generate the following markdown files. Output each one separated by a line: ===FILE: filename.md===

## Files to Generate

1. **CONTEXT_INDEX.md** — Master overview: project purpose, structure diagram, key patterns, quick reference table
2. **TECH_STACK.md** — Languages, frameworks, versions, dependencies, build/run commands
3. **ARCHITECTURE.md** — Architecture diagram (mermaid), component relationships, data flow, design decisions
4. **CODING_STANDARDS.md** — Naming conventions, file organization, error handling, logging patterns (extract from ACTUAL code)
5. **COMMON_PATTERNS.md** — Step-by-step guides: how to add a feature, endpoint, test, migration, etc.
6. **TESTING_STRATEGY.md** — Framework, patterns, naming, running tests, coverage
7. **DEPLOYMENT.md** — Build process, CI/CD, infrastructure, deployment steps
8. **API_REFERENCE.md** — Endpoints, interfaces, request/response formats (if applicable)

## Requirements

- Be SPECIFIC — cite actual file paths, class names, and code examples
- Use mermaid diagrams for architecture
- Make docs scannable with bullet points and tables
- Focus on patterns that help an AI agent make correct code changes

---

## Codebase File Structure

\`\`\`
$FILE_TREE
\`\`\`

## Detected Technologies

$(echo -e "$TECH")

## Test Files Found

$TEST_COUNT test files detected

## Code Samples

$CODE_SAMPLES

---

Now generate all 8 documentation files. Separate each with ===FILE: filename.md===
Start with CONTEXT_INDEX.md.
PROMPT_EOF

PROMPT_SIZE=$(du -k "$OUTPUT_FILE" | cut -f1)

echo ""
echo "========================================"
echo "  Setup prompt generated!"
echo "========================================"
echo ""
echo "  Output: $OUTPUT_FILE (${PROMPT_SIZE}KB)"
echo ""
echo "  NEXT STEPS:"
echo "  1. Open $OUTPUT_FILE"
echo "  2. Copy the entire contents"
echo "  3. Paste into your AI assistant (GitHub Copilot, Claude, ChatGPT)"
echo "  4. The AI will generate 8 markdown files"
echo "  5. Save each file to .agent/ in your repo root"
echo "  6. Commit: git add .agent/ && git commit -m 'Initialize .agent/ docs'"
echo ""
