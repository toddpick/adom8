<#
.SYNOPSIS
    Initializes a codebase for ADO-AI Agents by generating .agent/ documentation.

.DESCRIPTION
    This script scans your codebase, collects key information (file structure,
    code patterns, tech stack), and generates a context prompt. You then paste
    the prompt into an AI assistant (GitHub Copilot, Claude, ChatGPT) to generate
    the .agent/ documentation files that AI agents need.

    This is a ONE-TIME setup step. After generating, commit the .agent/ folder
    to your repository.

.PARAMETER RepoPath
    Path to the repository root. Defaults to current directory.

.PARAMETER OutputFile
    Path to write the AI prompt file. Defaults to .agent-setup-prompt.md in repo root.

.PARAMETER MaxSampleFiles
    Maximum number of code files to sample for pattern detection. Default: 15.

.EXAMPLE
    .\initialize-codebase.ps1
    .\initialize-codebase.ps1 -RepoPath "C:\MyProject" -MaxSampleFiles 20
#>

param(
    [string]$RepoPath = (Get-Location).Path,
    [string]$OutputFile = "",
    [int]$MaxSampleFiles = 15
)

$ErrorActionPreference = "Stop"

if (-not $OutputFile) {
    $OutputFile = Join-Path $RepoPath ".agent-setup-prompt.md"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ADO-AI Agents - Codebase Initializer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Scanning: $RepoPath"
Write-Host ""

# ─── Step 1: File Tree ───────────────────────────────────────────────

Write-Host "[1/5] Scanning file structure..." -ForegroundColor Yellow

$excludeDirs = @('.git', 'node_modules', 'bin', 'obj', '.vs', '.idea', 'packages',
    '__pycache__', '.terraform', 'dist', 'build', 'coverage', '.agent', '.ado',
    'TestResults', 'publish', '.nuget', 'artifacts')

$excludeExts = @('.dll', '.exe', '.pdb', '.cache', '.suo', '.user', '.lock',
    '.png', '.jpg', '.jpeg', '.gif', '.ico', '.svg', '.woff', '.woff2', '.ttf',
    '.eot', '.mp3', '.mp4', '.zip', '.gz', '.tar', '.7z', '.nupkg')

$allFiles = Get-ChildItem -Path $RepoPath -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $relativePath = $_.FullName.Substring($RepoPath.Length + 1)
    $parts = $relativePath.Split([IO.Path]::DirectorySeparatorChar)
    $excluded = $false
    foreach ($part in $parts) {
        if ($excludeDirs -contains $part) { $excluded = $true; break }
    }
    -not $excluded -and ($excludeExts -notcontains $_.Extension.ToLower())
}

$fileTree = $allFiles | ForEach-Object {
    $_.FullName.Substring($RepoPath.Length + 1).Replace('\', '/')
} | Sort-Object

Write-Host "  Found $($fileTree.Count) files"

# ─── Step 2: Detect Languages & Frameworks ───────────────────────────

Write-Host "[2/5] Detecting tech stack..." -ForegroundColor Yellow

$extCounts = @{}
$allFiles | ForEach-Object {
    $ext = $_.Extension.ToLower()
    if ($ext) { $extCounts[$ext] = ($extCounts[$ext] ?? 0) + 1 }
}

$techIndicators = @()

# Check for project/config files
$configFiles = @{
    '*.csproj'           = '.NET (C#)'
    '*.fsproj'           = '.NET (F#)'
    'package.json'       = 'Node.js'
    'requirements.txt'   = 'Python'
    'Pipfile'            = 'Python (Pipenv)'
    'pyproject.toml'     = 'Python'
    'go.mod'             = 'Go'
    'Cargo.toml'         = 'Rust'
    'pom.xml'            = 'Java (Maven)'
    'build.gradle'       = 'Java/Kotlin (Gradle)'
    'Gemfile'            = 'Ruby'
    'composer.json'      = 'PHP'
    'Dockerfile'         = 'Docker'
    'docker-compose.yml' = 'Docker Compose'
    'terraform.tf'       = 'Terraform'
    '*.tf'               = 'Terraform'
    'bicep'              = 'Azure Bicep'
    'tsconfig.json'      = 'TypeScript'
    'angular.json'       = 'Angular'
    'next.config.*'      = 'Next.js'
    'vite.config.*'      = 'Vite'
}

foreach ($pattern in $configFiles.Keys) {
    $found = Get-ChildItem -Path $RepoPath -Filter $pattern -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $techIndicators += $configFiles[$pattern] }
}

$techIndicators = $techIndicators | Select-Object -Unique
Write-Host "  Detected: $($techIndicators -join ', ')"

# ─── Step 3: Sample Code Files ──────────────────────────────────────

Write-Host "[3/5] Sampling code files for pattern detection..." -ForegroundColor Yellow

$codeExts = @('.cs', '.ts', '.js', '.py', '.go', '.rs', '.java', '.kt', '.rb',
    '.php', '.tsx', '.jsx', '.fs', '.swift', '.cpp', '.c', '.h')

$codeFiles = $allFiles | Where-Object { $codeExts -contains $_.Extension.ToLower() }

# Prioritize: entry points, DI setup, models, services, tests
$priorityPatterns = @('Program', 'Startup', 'Main', 'index', 'app', 'server',
    'Service', 'Controller', 'Model', 'Test', 'Spec', 'Config', 'Options')

$prioritized = $codeFiles | Sort-Object {
    $name = $_.BaseName
    $priority = 100
    for ($i = 0; $i -lt $priorityPatterns.Count; $i++) {
        if ($name -like "*$($priorityPatterns[$i])*") { $priority = $i; break }
    }
    $priority
} | Select-Object -First $MaxSampleFiles

$samples = @()
foreach ($file in $prioritized) {
    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($content -and $content.Length -lt 30000) {
        $lines = $content -split "`n"
        $truncated = ($lines | Select-Object -First 120) -join "`n"
        $relPath = $file.FullName.Substring($RepoPath.Length + 1).Replace('\', '/')
        $samples += @{ Path = $relPath; Content = $truncated }
    }
}

Write-Host "  Sampled $($samples.Count) files"

# ─── Step 4: Check for Tests ────────────────────────────────────────

Write-Host "[4/5] Detecting test setup..." -ForegroundColor Yellow

$testFiles = $allFiles | Where-Object {
    $_.Name -match '(Test|Spec|_test|_spec)\.' -or
    $_.FullName -match '(tests?|specs?|__tests__|__specs__)[/\\]'
}
$testCount = ($testFiles | Measure-Object).Count
Write-Host "  Found $testCount test files"

# ─── Step 5: Generate Prompt ────────────────────────────────────────

Write-Host "[5/5] Generating AI prompt..." -ForegroundColor Yellow

$prompt = @"
# Codebase Analysis — Generate .agent/ Documentation

You are analyzing a codebase to generate documentation that AI coding agents will use as context.
Generate the following markdown files. Output each one separated by a line: ===FILE: filename.md===

## Files to Generate

1. **CONTEXT_INDEX.md** — Master overview: project purpose, structure diagram, key patterns, quick reference table
2. **TECH_STACK.md** — Languages, frameworks, versions, dependencies, build/run commands
3. **ARCHITECTURE.md** — Architecture diagram (mermaid), component relationships, data flow, design decisions
4. **CODING_STANDARDS.md** — Naming conventions, file organization, error handling, logging patterns (extract from ACTUAL code)
5. **COMMON_PATTERNS.md** — Step-by-step guides: how to add a feature, endpoint, test, migration, etc.
6. **TESTING_STRATEGY.md** — Framework, patterns, naming conventions, running tests, coverage approach
7. **DEPLOYMENT.md** — Build process, CI/CD, infrastructure, deployment steps, configuration
8. **API_REFERENCE.md** — Endpoints, interfaces, request/response formats (if applicable)

## Requirements

- Be SPECIFIC — cite actual file paths, class names, and code examples from the codebase
- Use mermaid diagrams for architecture visualization
- Make docs scannable with bullet points and tables
- Focus on patterns that help an AI agent make correct code changes
- Each file should be self-contained but cross-reference others

---

## Codebase File Structure

``````
$($fileTree -join "`n")
``````

## Detected Technologies

$($techIndicators -join "`n")

## File Extension Distribution

$(($extCounts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 15 | ForEach-Object { "- $($_.Key): $($_.Value) files" }) -join "`n")

## Test Files Found

$testCount test files detected

## Code Samples

$($samples | ForEach-Object { "### $($_.Path)`n``````$($_.Path.Split('.')[-1])`n$($_.Content)`n```````n" } | Out-String)

---

Now generate all 8 documentation files. Separate each with ===FILE: filename.md===
Start with CONTEXT_INDEX.md.
"@

$prompt | Out-File -FilePath $OutputFile -Encoding utf8

$promptSizeKB = [math]::Round((Get-Item $OutputFile).Length / 1KB, 1)

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Setup prompt generated!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Output: $OutputFile ($promptSizeKB KB)"
Write-Host ""
Write-Host "  NEXT STEPS:" -ForegroundColor Cyan
Write-Host "  1. Open $OutputFile"
Write-Host "  2. Copy the entire contents"
Write-Host "  3. Paste into your AI assistant (GitHub Copilot, Claude, ChatGPT)"
Write-Host "  4. The AI will generate 8 markdown files"
Write-Host "  5. Save each file to .agent/ in your repo root"
Write-Host "  6. Commit: git add .agent/ && git commit -m 'Initialize .agent/ docs'"
Write-Host ""
Write-Host "  TIP: If using GitHub Copilot in VS Code, you can also ask:" -ForegroundColor DarkGray
Write-Host '  "Read .agent-setup-prompt.md and generate the .agent/ docs"' -ForegroundColor DarkGray
Write-Host ""
