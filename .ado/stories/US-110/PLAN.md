# Planning Analysis for US-110

## Story Overview

**ID:** US-110  
**Title:** Initialize Codebase Intelligence Documentation  
**State:** Needs Revision  
**Created:** 2026-02-26

### Description
<h2>Codebase Intelligence: Initial Documentation Scan </h2>

<h3>Overview </h3>
<p>Scan the entire repository and generate comprehensive AI-optimized documentation in the <code>.agent/</code> folder. 
This documentation will be used by all subsequent AI agents to understand the codebase structure, patterns, and conventions. </p>

<h3>What to Create </h3>
<p>Create a <code>.agent/</code> folder at the repository root containing the following files: </p>

<h4>Core Documentation Files </h4>
<ol>
<li><strong>CONTEXT_INDEX.md</strong> — Master overview of the project: purpose, high-level architecture, directory structure, 
key entry points, main features, quick reference for common tasks. This is the first file AI agents read. </li>

<li><strong>TECH_STACK.md</strong> — Languages, frameworks, versions, package managers, build tools, runtime requirements, 
key dependencies and their purposes. Include exact version numbers from project files. </li>

<li><strong>ARCHITECTURE.md</strong> — Architecture pattern (MVC, Clean Architecture, etc.), component relationships, 
data flow diagrams using Mermaid syntax, layer responsibilities, key design decisions. 
Include at least one Mermaid diagram showing the high-level system architecture. </li>

<li><strong>CODING_STANDARDS.md</strong> — Naming conventions (extracted from actual code patterns), file organization, 
error handling patterns, logging approach, dependency injection patterns, code formatting standards. 
Base these on the ACTUAL patterns found in the code, not generic best practices. </li>

<li><strong>COMMON_PATTERNS.md</strong> — Step-by-step how-to guides: how to add a new feature, add an API endpoint, 
add a UI component, add a database migration, write tests. Include specific file paths and code examples 
from the actual codebase. </li>

<li><strong>TESTING_STRATEGY.md</strong> — Test framework(s) used, test naming conventions, test organization, 
how to run tests, mocking patterns, coverage approach, integration vs unit test boundaries. </li>

<li><strong>DEPLOYMENT.md</strong> — Build process, CI/CD pipeline structure, infrastructure (Terraform, ARM, etc.), 
deployment steps, environment configuration, secrets management approach. </li>
</ol>

<h4>Conditional Documentation Files </h4>
<ol>
<li><strong>API_REFERENCE.md</strong> — (Create if the project has API endpoints) All endpoints with routes, 
HTTP methods, request/response formats, authentication requirements, error codes. </li>

<li><strong>DATABASE_SCHEMA.md</strong> — (Create if the project has a database) Tables/collections, relationships, 
ORM patterns, migration approach, connection management. </li>
</ol>

<h4>Feature Documentation </h4>
<p>Create a <code>.agent/FEATURES/</code> subfolder. For each major feature area detected in the codebase, 
create a separate markdown file (e.g., <code>authentication.md</code>, <code>data-access.md</code>, <code>notifications.md</code>). </p>
<p>Each feature file should contain: overview, key files involved, architecture/data flow (with Mermaid diagrams), 
configuration requirements, how to modify/extend it, testing approach for that feature. </p>
<p>Detect features by examining: folder structure, service/controller names, keyword patterns in code 
(auth, payment, notification, search, admin, reporting, etc.). </p>

<h4>Metadata Files </h4>
<ol>
<li><strong>metadata.json</strong> — JSON file with analysis stats:
<pre>{
  &quot;lastAnalysis&quot;: &quot;ISO-8601 timestamp&quot;,
  &quot;filesAnalyzed&quot;: number,
  &quot;linesOfCode&quot;: number,
  &quot;featuresDocumented&quot;: number,
  &quot;languagesDetected&quot;: [&quot;lang1&quot;, &quot;lang2&quot;],
  &quot;primaryFramework&quot;: &quot;framework name&quot;,
  &quot;documentationSizeKB&quot;: number,
  &quot;featuresDocumentedList&quot;: [&quot;feature1&quot;, &quot;feature2&quot;]
}</pre> </li>

<li><strong>README.md</strong> — Human-readable guide explaining what the .agent/ folder is, 
why it exists, and how AI agents use it. Include analysis statistics. </li>
</ol>

<h3>How to Scan </h3>
<ol>
<li>Map the complete file/folder tree (exclude .git, node_modules, bin, obj, dist, build, vendor, 
__pycache__, .vs, .idea, packages, and other build output directories). </li>
<li>Detect the tech stack from project files (.csproj, package.json, requirements.txt, go.mod, Cargo.toml, 
pom.xml, build.gradle, Gemfile, etc.). </li>
<li>Sample 30-50 key source files (prioritize: entry points, controllers, services, repositories, models, 
configuration files, tests, middleware). Read enough of each file to understand patterns. </li>
<li>Detect coding patterns: naming conventions, error handling, logging, DI registration, 
file organization, testing approaches. </li>
<li>Identify features from folder names, class names, and code keywords. </li>
<li>Generate all documentation files with specific file paths, code examples, and Mermaid diagrams 
based on the ACTUAL code — not generic templates. </li>
</ol>

<h3>Important Guidelines </h3>
<ul>
<li>All documentation must reference ACTUAL file paths and code patterns from this specific repository. </li>
<li>Include Mermaid diagrams in ARCHITECTURE.md and feature docs showing real component relationships. </li>
<li>CODING_STANDARDS.md must be extracted from observed patterns, not generic guidelines. </li>
<li>COMMON_PATTERNS.md must include real file paths for &quot;how to add X&quot; guides. </li>
<li>Commit all files to the <code>.agent/</code> folder on the main branch. </li>
<li>Do NOT modify any existing source code — only create files in <code>.agent/</code>. </li>
</ul>

<h3>Acceptance Criteria </h3>
<ul>
<li>[ ] .agent/ folder exists at repository root with all core documentation files </li>
<li>[ ] CONTEXT_INDEX.md provides accurate project overview with real structure </li>
<li>[ ] ARCHITECTURE.md contains at least one Mermaid diagram of system architecture </li>
<li>[ ] CODING_STANDARDS.md reflects actual code conventions (not generic) </li>
<li>[ ] COMMON_PATTERNS.md has step-by-step guides with real file paths </li>
<li>[ ] FEATURES/ subfolder has per-feature documentation for detected features </li>
<li>[ ] metadata.json has accurate analysis statistics </li>
<li>[ ] No existing source code was modified </li>
</ul>

### Acceptance Criteria
No acceptance criteria

---

## Technical Analysis

### Problem Analysis
This story requires creating a comprehensive codebase intelligence system that scans the entire repository and generates AI-optimized documentation in a .agent/ folder. The system needs to analyze file structure, detect technology stack, identify coding patterns, extract features, and generate multiple documentation files with real examples and Mermaid diagrams. The documentation will serve as context for future AI agents working on the codebase.

### Recommended Approach
Implement a new agent service (CodebaseIntelligenceAgentService) that performs repository analysis in phases: 1) File system mapping with exclusion filters, 2) Tech stack detection from project files, 3) Source code sampling and pattern analysis, 4) Feature detection via naming conventions and keywords, 5) Documentation generation using Scriban templates, 6) Metadata collection and JSON generation, 7) Git commit of all generated files. The service will use existing IGitOperations for file I/O and LibGit2Sharp for commits.

### Affected Files

- `src/AIAgents.Functions/Agents/CodebaseIntelligenceAgentService.cs`

- `src/AIAgents.Core/Services/CodebaseAnalyzer.cs`

- `src/AIAgents.Core/Services/TechStackDetector.cs`

- `src/AIAgents.Core/Services/FeatureDetector.cs`

- `src/AIAgents.Core/Models/CodebaseAnalysisResult.cs`

- `src/AIAgents.Core/Models/TechStackInfo.cs`

- `src/AIAgents.Core/Models/FeatureInfo.cs`

- `src/AIAgents.Core/Templates/codebase-intelligence/*.scriban`

- `src/AIAgents.Functions/Functions/CodebaseIntelligenceFunction.cs`

- `src/AIAgents.Functions/Program.cs`

- `src/AIAgents.Functions.Tests/Agents/CodebaseIntelligenceAgentServiceTests.cs`


### Complexity Estimate
**Story Points:** 13

### Architecture Considerations
The solution follows the existing agent pattern with a new CodebaseIntelligenceAgentService that orchestrates multiple analysis services. CodebaseAnalyzer handles file system traversal and sampling, TechStackDetector identifies frameworks from project files, FeatureDetector uses heuristics to identify functional areas. All services use dependency injection and follow existing error handling patterns. Documentation generation uses Scriban templates for consistency with other agents.

---

## Implementation Plan

### Sub-Tasks

1. Create CodebaseAnalyzer service for file system mapping and exclusion filtering

2. Implement TechStackDetector to parse project files (csproj, package.json, etc.)

3. Build FeatureDetector with keyword and naming pattern recognition

4. Create Scriban templates for all documentation files (9 core + conditional files)

5. Implement CodebaseIntelligenceAgentService with full analysis pipeline

6. Add HTTP function endpoint for triggering codebase analysis

7. Create comprehensive unit tests for all analysis components

8. Update Program.cs with new service registrations


### Dependencies


- Existing IGitOperations interface for file I/O operations

- LibGit2Sharp for Git operations and commits

- Scriban template engine for documentation generation

- System.IO for file system operations

- System.Text.Json for metadata.json generation

- Existing logging and error handling infrastructure



---

## Risk Assessment

### Identified Risks

- Large repositories may cause memory issues during analysis - need streaming/batching approach

- File system permissions may prevent reading certain directories

- Analysis time may exceed Azure Functions timeout limits for very large codebases

- Generated documentation size may be very large for complex repositories


---

## Assumptions Made

- Repository structure follows standard conventions for tech stack detection

- Feature detection heuristics will work reasonably well across different codebases

- Generated documentation will fit within reasonable file size limits

- Mermaid diagram generation can be done programmatically from code analysis

- Repository has sufficient permissions for reading all source files


---

## Testing Strategy
Unit tests for each analysis service with mock file systems and sample project files. Integration tests with real repository structures. Test coverage for all supported project file types and feature detection patterns. Performance tests with large file sets. Error handling tests for permission issues and malformed project files.

---

*Generated by Planning Agent*  
*Timestamp: 2026-02-26T04:45:40.6203055Z*
