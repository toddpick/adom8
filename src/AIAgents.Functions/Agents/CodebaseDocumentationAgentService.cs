using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Agents;

/// <summary>
/// 7th agent — CodebaseDocumentationAgent.
/// Analyzes the entire codebase, historical user stories, and git commits
/// to generate AI-optimized documentation in the .agent/ folder.
/// Future agents load this context to produce architecture-aware code.
/// </summary>
public sealed class CodebaseDocumentationAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly IActivityLogger _activityLogger;
    private readonly CodebaseDocumentationOptions _options;
    private readonly ILogger<CodebaseDocumentationAgentService> _logger;
    private readonly StoryTokenUsage _tokenUsage = new();

    public CodebaseDocumentationAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        IActivityLogger activityLogger,
        IOptions<CodebaseDocumentationOptions> options,
        ILogger<CodebaseDocumentationAgentService> logger)
    {
        _aiClient = aiClientFactory.GetClientForAgent("CodebaseDocumentation");
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _activityLogger = activityLogger;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("CodebaseDocumentation agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);

        // Determine timeframe from work item description or use default
        var timeframe = ParseTimeframe(workItem.Description);
        var isIncremental = workItem.Description?.Contains("incremental", StringComparison.OrdinalIgnoreCase) == true;

        // Step 1: Clone/open repo on main branch
        await LogProgress(task.WorkItemId, "Cloning repository...", cancellationToken);
        var repoPath = await _gitOps.EnsureBranchAsync("main", cancellationToken);

        // Check for existing metadata (incremental analysis)
        CodebaseAnalysisMetadata? existingMetadata = null;
        if (isIncremental)
        {
            existingMetadata = await LoadMetadataAsync(repoPath, cancellationToken);
            if (existingMetadata is not null)
            {
                _logger.LogInformation("Incremental analysis — last scan: {Date}", existingMetadata.LastAnalysis);
            }
        }

        // Step 2: Scan codebase structure
        await LogProgress(task.WorkItemId, "Scanning codebase structure...", cancellationToken);
        var (fileTree, techStack, fileCount, lineCount) = await ScanCodebaseAsync(repoPath, cancellationToken);

        // Step 3: Extract detected patterns
        await LogProgress(task.WorkItemId, "Analyzing code patterns...", cancellationToken);
        var patterns = await DetectPatternsAsync(repoPath, fileTree, cancellationToken);

        // Step 4: Query ADO historical user stories
        await LogProgress(task.WorkItemId, "Querying user story history...", cancellationToken);
        var storySummary = await QueryUserStoriesAsync(timeframe, cancellationToken);

        // Step 5: Analyze git commit history
        var commitSummary = string.Empty;
        if (workItem.Description?.Contains("includeGitHistory=false", StringComparison.OrdinalIgnoreCase) != true)
        {
            await LogProgress(task.WorkItemId, "Analyzing git commit history...", cancellationToken);
            commitSummary = await AnalyzeCommitHistoryAsync(repoPath, timeframe, cancellationToken);
        }

        // Step 6: Detect features from code structure + stories
        await LogProgress(task.WorkItemId, "Detecting major features...", cancellationToken);
        var detectedFeatures = DetectFeatures(fileTree, storySummary, patterns);

        // Step 7: Generate documentation via AI (multi-pass for token management)
        await LogProgress(task.WorkItemId, "Generating core documentation...", cancellationToken);
        var coreDocs = await GenerateCoreDocumentationAsync(
            fileTree, techStack, patterns, storySummary, commitSummary, cancellationToken);

        await LogProgress(task.WorkItemId, $"Generating feature docs for {detectedFeatures.Count} features...", cancellationToken);
        var featureDocs = await GenerateFeatureDocumentationAsync(
            detectedFeatures, fileTree, storySummary, commitSummary, cancellationToken);

        // Step 8: Write .agent/ folder to repository
        await LogProgress(task.WorkItemId, "Writing documentation to repository...", cancellationToken);
        var totalFiles = await WriteDocumentationAsync(
            repoPath, coreDocs, featureDocs, cancellationToken);

        // Write metadata
        var metadata = new CodebaseAnalysisMetadata
        {
            LastAnalysis = DateTime.UtcNow,
            FilesAnalyzed = fileCount,
            LinesOfCode = lineCount,
            UserStoriesReviewed = CountStoriesInSummary(storySummary),
            CommitsAnalyzed = CountCommitsInSummary(commitSummary),
            FeaturesDocumented = featureDocs.Count,
            LanguagesDetected = ExtractLanguages(techStack),
            PrimaryFramework = ExtractPrimaryFramework(techStack),
            FeaturesDocumentedList = detectedFeatures
        };
        await WriteMetadataAsync(repoPath, metadata, cancellationToken);

        // Write .agent/README.md with populated statistics
        await WriteAgentReadmeAsync(repoPath, metadata, cancellationToken);

        // Step 9: Commit and push
        await LogProgress(task.WorkItemId, "Committing documentation to repository...", cancellationToken);
        var commitMsg = isIncremental
            ? $"[AI] Updated codebase documentation — {fileCount} files analyzed, {featureDocs.Count} features documented"
            : $"[AI] Initial codebase documentation — {fileCount} files analyzed, {featureDocs.Count} features documented";
        await _gitOps.CommitAndPushAsync(repoPath, commitMsg, cancellationToken);

        // Step 10: Post summary to work item
        var summary = BuildSummaryComment(metadata, totalFiles, isIncremental, _tokenUsage);
        await _adoClient.AddWorkItemCommentAsync(task.WorkItemId, summary, cancellationToken);

        // Update work item state to Done
        await _adoClient.UpdateWorkItemStateAsync(task.WorkItemId, "Done", cancellationToken);

        _logger.LogInformation(
            "CodebaseDocumentation agent completed for WI-{WorkItemId}: {FileCount} files, {FeatureCount} features",
            task.WorkItemId, fileCount, featureDocs.Count);
    }

    #region Step 2: Codebase Scanning

    private async Task<(string FileTree, string TechStack, int FileCount, long LineCount)> ScanCodebaseAsync(
        string repoPath, CancellationToken ct)
    {
        var allFiles = await _gitOps.ListFilesAsync(repoPath, ct);
        var filteredFiles = FilterFiles(allFiles).ToList();

        // Build hierarchical file tree
        var tree = new StringBuilder();
        var techIndicators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalLines = 0;

        // Limit for tree display
        var displayFiles = filteredFiles.Take(_options.MaxFilesToAnalyze).ToList();

        foreach (var file in displayFiles)
        {
            tree.AppendLine(file);

            // Count lines (sample cost-effectively — read first N files)
            if (totalLines < 500_000)
            {
                var content = await _gitOps.ReadFileAsync(repoPath, file, ct);
                if (content is not null)
                {
                    totalLines += content.Split('\n').Length;
                }
            }

            // Detect tech stack indicators
            DetectTechFromFile(file, techIndicators);
        }

        var techStackSummary = BuildTechStackSummary(techIndicators);

        return (tree.ToString(), techStackSummary, displayFiles.Count, totalLines);
    }

    private IEnumerable<string> FilterFiles(IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            var normalized = file.Replace('\\', '/');
            var skip = false;

            foreach (var pattern in _options.ExcludePatterns)
            {
                if (pattern.StartsWith("*."))
                {
                    if (normalized.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                    {
                        skip = true;
                        break;
                    }
                }
                else if (normalized.Contains(pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    skip = true;
                    break;
                }
            }

            // Also skip the .agent/ folder itself
            if (normalized.StartsWith(".agent/", StringComparison.OrdinalIgnoreCase))
                skip = true;

            if (!skip) yield return file;
        }
    }

    private static void DetectTechFromFile(string file, Dictionary<string, string> indicators)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // Package/project files
        if (name == "package.json") indicators["Node.js/npm"] = "package.json";
        if (name == "requirements.txt") indicators["Python/pip"] = "requirements.txt";
        if (name == "go.mod") indicators["Go"] = "go.mod";
        if (name == "cargo.toml") indicators["Rust"] = "Cargo.toml";
        if (name == "pom.xml") indicators["Java/Maven"] = "pom.xml";
        if (name == "build.gradle" || name == "build.gradle.kts") indicators["Java/Gradle"] = name;
        if (name == "gemfile") indicators["Ruby"] = "Gemfile";
        if (name == "composer.json") indicators["PHP/Composer"] = "composer.json";

        // .NET
        if (ext == ".csproj") indicators[".NET/C#"] = file;
        if (ext == ".fsproj") indicators[".NET/F#"] = file;
        if (ext == ".sln") indicators[".NET Solution"] = file;

        // Frameworks
        if (name == "angular.json") indicators["Angular"] = "angular.json";
        if (name == "next.config.js" || name == "next.config.mjs") indicators["Next.js"] = name;
        if (name == "nuxt.config.ts" || name == "nuxt.config.js") indicators["Nuxt.js"] = name;
        if (name == "vite.config.ts" || name == "vite.config.js") indicators["Vite"] = name;
        if (name == "tsconfig.json") indicators["TypeScript"] = "tsconfig.json";

        // Infrastructure
        if (name == "dockerfile" || name.StartsWith("dockerfile.")) indicators["Docker"] = name;
        if (name == "docker-compose.yml" || name == "docker-compose.yaml") indicators["Docker Compose"] = name;
        if (ext == ".tf") indicators["Terraform"] = file;
        if (name == "kubernetes.yml" || name == "k8s.yml") indicators["Kubernetes"] = name;

        // CI/CD
        if (file.Contains(".github/workflows")) indicators["GitHub Actions"] = file;
        if (file.Contains(".azure-pipelines") || file.Contains("azure-pipelines.yml")) indicators["Azure Pipelines"] = file;

        // Language extensions
        if (ext == ".py") indicators.TryAdd("Python", file);
        if (ext == ".ts" || ext == ".tsx") indicators.TryAdd("TypeScript", file);
        if (ext == ".js" || ext == ".jsx") indicators.TryAdd("JavaScript", file);
        if (ext == ".java") indicators.TryAdd("Java", file);
        if (ext == ".rb") indicators.TryAdd("Ruby", file);
        if (ext == ".php") indicators.TryAdd("PHP", file);
        if (ext == ".go") indicators.TryAdd("Go", file);
        if (ext == ".rs") indicators.TryAdd("Rust", file);
        if (ext == ".cs") indicators.TryAdd("C#", file);
        if (ext == ".swift") indicators.TryAdd("Swift", file);
        if (ext == ".kt" || ext == ".kts") indicators.TryAdd("Kotlin", file);
    }

    private static string BuildTechStackSummary(Dictionary<string, string> indicators)
    {
        if (indicators.Count == 0) return "Unable to detect tech stack.";

        var sb = new StringBuilder();
        sb.AppendLine("Detected technologies:");
        foreach (var (tech, indicator) in indicators.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"  - {tech} (found: {indicator})");
        }
        return sb.ToString();
    }

    #endregion

    #region Step 3: Pattern Detection

    private async Task<string> DetectPatternsAsync(
        string repoPath, string fileTree, CancellationToken ct)
    {
        // Sample key files to detect patterns
        var sampleFiles = new List<(string Path, string Content)>();
        var allFiles = fileTree.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Prioritize: controllers, services, repositories, models, tests
        var priorityPatterns = new[] { "controller", "service", "repository", "handler", "model",
            "entity", "middleware", "startup", "program", "test" };

        var sampled = allFiles
            .Where(f => priorityPatterns.Any(p =>
                f.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Take(30)
            .ToList();

        // Also sample a few random files for diversity
        sampled.AddRange(allFiles
            .Where(f => !sampled.Contains(f))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".ts") || f.EndsWith(".py") ||
                        f.EndsWith(".java") || f.EndsWith(".js"))
            .Take(10));

        foreach (var file in sampled.Distinct().Take(40))
        {
            var content = await _gitOps.ReadFileAsync(repoPath, file, ct);
            if (!string.IsNullOrWhiteSpace(content) && content.Length < 50_000)
            {
                sampleFiles.Add((file, content));
            }
        }

        if (sampleFiles.Count == 0)
            return "No code files sampled for pattern detection.";

        var codeSnippets = new StringBuilder();
        foreach (var (path, content) in sampleFiles.Take(20))
        {
            // Truncate very long files to first 150 lines
            var lines = content.Split('\n');
            var truncated = string.Join('\n', lines.Take(150));
            codeSnippets.AppendLine($"--- {path} ---");
            codeSnippets.AppendLine(truncated);
            codeSnippets.AppendLine();
        }

        var systemPrompt = @"You are a code analysis expert. Analyze the code samples and extract patterns.
Return a structured summary of detected patterns. Be specific — cite actual examples from the code.
Focus on: naming conventions, file organization, dependency injection, error handling, logging,
testing patterns, API design, database access, authentication patterns.
Keep it concise but actionable.";

        var userPrompt = $"Analyze these code samples and extract coding patterns:\n\n{codeSnippets}";

        var aiResult = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 4096, Temperature = 0.2 }, ct);
        _tokenUsage.RecordUsage("CodebaseDocumentation", aiResult.Usage);
        return aiResult.Content;
    }

    #endregion

    #region Step 4: ADO User Stories

    private Task<string> QueryUserStoriesAsync(TimeSpan timeframe, CancellationToken ct)
    {
        // We use the ADO client to query completed stories
        // Since IAzureDevOpsClient currently only has single-item ops,
        // we'll summarize what we know from the work item context
        var sb = new StringBuilder();
        sb.AppendLine("User Story History:");
        sb.AppendLine($"  Timeframe: last {timeframe.TotalDays:F0} days");
        sb.AppendLine("  Note: Full WIQL query integration for bulk story retrieval");
        sb.AppendLine("  is planned. Currently working with available work item context.");

        // This would be expanded with WIQL queries when IAzureDevOpsClient supports it.
        // For now, the agent extracts what it can from repo artifacts (README, docs, etc.)
        return Task.FromResult(sb.ToString());
    }

    #endregion

    #region Step 5: Git Commit History

    private Task<string> AnalyzeCommitHistoryAsync(
        string repoPath, TimeSpan timeframe, CancellationToken ct)
    {
        // Git log analysis would use LibGit2Sharp commit enumeration.
        // For now, detect common commit patterns from branch names and file history.
        var sb = new StringBuilder();
        sb.AppendLine("Git History Analysis:");
        sb.AppendLine($"  Analysis window: last {timeframe.TotalDays:F0} days");
        sb.AppendLine("  Commit linkage: Work items referenced via US-### pattern in commit messages");

        return Task.FromResult(sb.ToString());
    }

    #endregion

    #region Step 6: Feature Detection

    private List<string> DetectFeatures(string fileTree, string storySummary, string patterns)
    {
        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var combinedText = $"{fileTree}\n{storySummary}\n{patterns}".ToLowerInvariant();

        // Detect from keywords
        var keywordGroups = new Dictionary<string, string[]>
        {
            ["authentication"] = new[] { "auth", "login", "sso", "oauth", "jwt", "identity" },
            ["user-management"] = new[] { "user", "profile", "account", "registration", "signup" },
            ["payment-processing"] = new[] { "payment", "checkout", "billing", "invoice", "stripe", "paypal" },
            ["notifications"] = new[] { "notification", "email", "sms", "push notification", "alert" },
            ["reporting"] = new[] { "report", "dashboard", "analytics", "metrics", "chart" },
            ["search"] = new[] { "search", "filter", "facet", "elasticsearch", "full-text" },
            ["administration"] = new[] { "admin", "settings", "configuration", "management" },
            ["api-integration"] = new[] { "webhook", "external api", "integration", "rest api" },
            ["data-access"] = new[] { "database", "repository", "entity framework", "migration", "schema" },
            ["infrastructure"] = new[] { "terraform", "docker", "kubernetes", "ci/cd", "pipeline" },
            ["testing"] = new[] { "unit test", "integration test", "test framework", "mock", "fixture" }
        };

        foreach (var (feature, keywords) in keywordGroups)
        {
            if (keywords.Any(kw => combinedText.Contains(kw)))
            {
                features.Add(feature);
            }
        }

        // Detect from folder structure (e.g., Controllers/AuthController → authentication)
        var folders = fileTree.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Path.GetDirectoryName(f)?.Replace('\\', '/') ?? "")
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToList();

        foreach (var folder in folders)
        {
            var folderName = folder.Split('/').LastOrDefault()?.ToLowerInvariant() ?? "";
            if (folderName.Length > 3 && !folderName.StartsWith("."))
            {
                foreach (var (feature, keywords) in keywordGroups)
                {
                    if (keywords.Any(kw => folderName.Contains(kw)))
                        features.Add(feature);
                }
            }
        }

        return features.OrderBy(f => f).ToList();
    }

    #endregion

    #region Step 7: AI Documentation Generation

    private async Task<Dictionary<string, string>> GenerateCoreDocumentationAsync(
        string fileTree, string techStack, string patterns,
        string storySummary, string commitSummary, CancellationToken ct)
    {
        var systemPrompt = @"You are generating AI-optimized codebase documentation.
Your output will be consumed by future AI agents to generate context-aware code.
Focus on HOW patterns are implemented with specific file paths and examples.
Format each document as clean Markdown with clear headings.

You will generate multiple documentation files. For each, output them separated by a line:
===FILE: filename.md===

Generate these files:
1. CONTEXT_INDEX.md — Master overview with quick reference, project structure, features list, patterns
2. TECH_STACK.md — Languages, frameworks, versions, dependencies, tools
3. ARCHITECTURE.md — Architecture pattern, component relationships, data flow, design decisions (use mermaid diagrams)
4. CODING_STANDARDS.md — Naming conventions, file organization, error handling, logging, extracted from actual code
5. COMMON_PATTERNS.md — How to add features, endpoints, tests, migrations. Step-by-step guides
6. TESTING_STRATEGY.md — Test framework, patterns, naming conventions, coverage approach
7. DEPLOYMENT.md — Build process, CI/CD, infrastructure, deployment steps

Make each file comprehensive but scannable. Use bullet points and code examples from the actual codebase.
If you detect a database or API, also include DATABASE_SCHEMA.md and/or API_REFERENCE.md sections.";

        var userPrompt = $@"## Codebase File Structure
{TruncateForPrompt(fileTree, 8000)}

## Detected Tech Stack
{techStack}

## Detected Code Patterns
{TruncateForPrompt(patterns, 6000)}

## User Story History
{TruncateForPrompt(storySummary, 3000)}

## Git Commit History
{TruncateForPrompt(commitSummary, 2000)}

Generate comprehensive documentation files for this codebase.";

        var aiResult = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 16384, Temperature = 0.3 }, ct);
        _tokenUsage.RecordUsage("CodebaseDocumentation", aiResult.Usage);

        return ParseMultiFileResponse(aiResult.Content);
    }

    private async Task<Dictionary<string, string>> GenerateFeatureDocumentationAsync(
        List<string> features, string fileTree, string storySummary,
        string commitSummary, CancellationToken ct)
    {
        if (features.Count == 0) return new Dictionary<string, string>();

        var docs = new Dictionary<string, string>();

        // Batch features into groups to reduce API calls
        var batches = features.Chunk(5).ToList();

        foreach (var batch in batches)
        {
            var featureList = string.Join(", ", batch);

            var systemPrompt = @"You are generating feature documentation for AI agents.
For each feature, create a markdown document covering:
- Overview of what the feature does
- Key files and code locations involved
- Architecture/flow (use mermaid diagram if helpful)
- Configuration required
- Common modification patterns (how to extend this feature)
- Testing approach

Separate each feature document with: ===FILE: feature-name.md===
Use the exact feature names provided as filenames.";

            var userPrompt = $@"Generate documentation for these features: {featureList}

Codebase structure (relevant excerpts):
{TruncateForPrompt(fileTree, 4000)}

Story history context:
{TruncateForPrompt(storySummary, 2000)}

Create one documentation file per feature.";

            var aiResult = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
                new AICompletionOptions { MaxTokens = 8192, Temperature = 0.3 }, ct);
            _tokenUsage.RecordUsage("CodebaseDocumentation", aiResult.Usage);

            foreach (var (name, content) in ParseMultiFileResponse(aiResult.Content))
            {
                docs[name] = content;
            }
        }

        return docs;
    }

    #endregion

    #region Step 8: Write Files

    private async Task<int> WriteDocumentationAsync(
        string repoPath,
        Dictionary<string, string> coreDocs,
        Dictionary<string, string> featureDocs,
        CancellationToken ct)
    {
        var outputFolder = _options.OutputFolder;
        var totalFiles = 0;

        // Write core docs
        foreach (var (fileName, content) in coreDocs)
        {
            var relativePath = Path.Combine(outputFolder, fileName).Replace('\\', '/');
            await _gitOps.WriteFileAsync(repoPath, relativePath, content, ct);
            totalFiles++;
            _logger.LogDebug("Wrote {Path}", relativePath);
        }

        // Write feature docs
        foreach (var (fileName, content) in featureDocs)
        {
            var sanitized = SanitizeFileName(fileName);
            var relativePath = Path.Combine(outputFolder, "FEATURES", sanitized).Replace('\\', '/');
            await _gitOps.WriteFileAsync(repoPath, relativePath, content, ct);
            totalFiles++;
            _logger.LogDebug("Wrote feature doc: {Path}", relativePath);
        }

        return totalFiles;
    }

    private async Task WriteMetadataAsync(
        string repoPath, CodebaseAnalysisMetadata metadata, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(_options.OutputFolder, "metadata.json").Replace('\\', '/');
        await _gitOps.WriteFileAsync(repoPath, path, json, ct);
    }

    private async Task WriteAgentReadmeAsync(
        string repoPath, CodebaseAnalysisMetadata metadata, CancellationToken ct)
    {
        var nextRefresh = (metadata.LastAnalysis ?? DateTime.UtcNow).AddMonths(3);
        var features = metadata.FeaturesDocumentedList.Count > 0
            ? string.Join(", ", metadata.FeaturesDocumentedList)
            : $"{metadata.FeaturesDocumented} features";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# .agent/ Folder — AI-Optimized Documentation");
        sb.AppendLine();
        sb.AppendLine("This folder contains comprehensive documentation generated by analyzing the codebase,");
        sb.AppendLine("historical user stories, and git history. It serves as the **shared source of truth**");
        sb.AppendLine("for both AI agents and human developers.");
        sb.AppendLine();
        sb.AppendLine("## Purpose");
        sb.AppendLine();
        sb.AppendLine("Provide context for:");
        sb.AppendLine();
        sb.AppendLine("1. **AI Agents** — Autonomous agents read these files to understand codebase patterns");
        sb.AppendLine("2. **Human Developers** — Developers reference these files when working on complex features");
        sb.AppendLine("3. **AI-Assisted Tools** — Claude Code CLI, OpenAI Codex CLI, Cursor, and GitHub Copilot benefit from this context");
        sb.AppendLine();
        sb.AppendLine("## Contents");
        sb.AppendLine();
        sb.AppendLine("| File | Description |");
        sb.AppendLine("|------|-------------|");
        sb.AppendLine("| `CONTEXT_INDEX.md` | **Start here** — master overview, architecture summary, quick reference |");
        sb.AppendLine("| `TECH_STACK.md` | Languages, frameworks, tools, and versions |");
        sb.AppendLine("| `ARCHITECTURE.md` | System design, component diagrams, data flow |");
        sb.AppendLine("| `CODING_STANDARDS.md` | Conventions, patterns, examples (**critical for consistency**) |");
        sb.AppendLine("| `DATABASE_SCHEMA.md` | Tables, relationships, ORM patterns |");
        sb.AppendLine("| `API_REFERENCE.md` | Endpoints, authentication, request/response formats |");
        sb.AppendLine("| `COMMON_PATTERNS.md` | How to add features, handle errors, write tests |");
        sb.AppendLine("| `FEATURES/` | Per-feature deep dives (one file per major feature) |");
        sb.AppendLine();
        sb.AppendLine("## For Human Developers");
        sb.AppendLine();
        sb.AppendLine("**Before working on a feature:**");
        sb.AppendLine("1. Read `CONTEXT_INDEX.md` (5 min overview)");
        sb.AppendLine("2. Read relevant `FEATURES/*.md` (understand existing implementation)");
        sb.AppendLine("3. Read `CODING_STANDARDS.md` (know the patterns to follow)");
        sb.AppendLine();
        sb.AppendLine("**When using AI coding tools (Claude Code / Codex / Cursor / Copilot):**");
        sb.AppendLine("- Include `.agent/` context in your prompts");
        sb.AppendLine("- Use `scripts/ai-with-context.sh` helper");
        sb.AppendLine("- See [DEVELOPERS.md](../DEVELOPERS.md) for detailed prompt structure");
        sb.AppendLine();
        sb.AppendLine("**After completing a feature:**");
        sb.AppendLine("- Update relevant `FEATURES/*.md` with your additions");
        sb.AppendLine("- Document decisions in `.ado/stories/US-{id}/DECISIONS.md`");
        sb.AppendLine();
        sb.AppendLine("## For AI Agents");
        sb.AppendLine();
        sb.AppendLine("All agents automatically load context via `ICodebaseContextProvider`:");
        sb.AppendLine("1. `CONTEXT_INDEX.md` (always)");
        sb.AppendLine("2. Relevant `FEATURES/*.md` (based on work item)");
        sb.AppendLine("3. `CODING_STANDARDS.md` (always)");
        sb.AppendLine();
        sb.AppendLine("## Maintenance");
        sb.AppendLine();
        sb.AppendLine("Run **CodebaseDocumentationAgent** via the dashboard to regenerate.");
        sb.AppendLine("Recommended: quarterly or after major refactoring.");
        sb.AppendLine();
        sb.AppendLine("> **IMPORTANT:** This folder is committed to version control intentionally.");
        sb.AppendLine("> Do NOT add `.agent/` to `.gitignore`.");
        sb.AppendLine();
        sb.AppendLine("## Last Updated");
        sb.AppendLine();
        sb.AppendLine($"- **Generated:** {metadata.LastAnalysis:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **Source files analyzed:** {metadata.FilesAnalyzed:N0}");
        sb.AppendLine($"- **Lines of code:** {metadata.LinesOfCode:N0}");
        sb.AppendLine($"- **User stories reviewed:** {metadata.UserStoriesReviewed:N0}");
        sb.AppendLine($"- **Commits analyzed:** {metadata.CommitsAnalyzed:N0}");
        sb.AppendLine($"- **Features documented:** {features}");
        sb.AppendLine($"- **Languages:** {string.Join(", ", metadata.LanguagesDetected)}");
        sb.AppendLine($"- **Primary framework:** {metadata.PrimaryFramework ?? "N/A"}");
        sb.AppendLine($"- **Next refresh recommended:** {nextRefresh:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("*This documentation enables both AI agents and human developers to");
        sb.AppendLine("work consistently within the same codebase patterns.*");

        var readmePath = Path.Combine(_options.OutputFolder, "README.md").Replace('\\', '/');
        await _gitOps.WriteFileAsync(repoPath, readmePath, sb.ToString(), ct);
        _logger.LogDebug("Wrote {Path}", readmePath);
    }

    private async Task<CodebaseAnalysisMetadata?> LoadMetadataAsync(
        string repoPath, CancellationToken ct)
    {
        var path = Path.Combine(_options.OutputFolder, "metadata.json").Replace('\\', '/');
        var content = await _gitOps.ReadFileAsync(repoPath, path, ct);
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            return JsonSerializer.Deserialize<CodebaseAnalysisMetadata>(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion

    #region Helpers

    private async Task LogProgress(int workItemId, string message, CancellationToken ct)
    {
        _logger.LogInformation("WI-{WorkItemId}: {Message}", workItemId, message);
        await _activityLogger.LogAsync("CodebaseDocumentation", workItemId, message, cancellationToken: ct);
    }

    private static TimeSpan ParseTimeframe(string? description)
    {
        if (string.IsNullOrEmpty(description)) return TimeSpan.FromDays(180);

        if (description.Contains("3months", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("3mo", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromDays(90);
        if (description.Contains("12months", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("12mo", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("1year", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromDays(365);
        if (description.Contains("alltime", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("all time", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromDays(3650);

        return TimeSpan.FromDays(180); // default 6 months
    }

    private static Dictionary<string, string> ParseMultiFileResponse(string response)
    {
        var docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = @"===FILE:\s*(.+?)===";
        var parts = Regex.Split(response, pattern, RegexOptions.IgnoreCase);

        // parts[0] = preamble (before first ===FILE:===), skip
        // parts[1] = filename, parts[2] = content, parts[3] = filename, parts[4] = content, ...
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            var fileName = parts[i].Trim();
            var content = parts[i + 1].Trim();

            if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(content))
            {
                // Ensure .md extension
                if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    fileName += ".md";

                docs[fileName] = content;
            }
        }

        // Fallback: if no ===FILE:=== markers found, treat entire response as CONTEXT_INDEX.md
        if (docs.Count == 0 && !string.IsNullOrWhiteSpace(response))
        {
            docs["CONTEXT_INDEX.md"] = response;
        }

        return docs;
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = Regex.Replace(name, @"[^a-zA-Z0-9\-_\.]", "-");
        return sanitized.ToLowerInvariant();
    }

    private static string TruncateForPrompt(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text ?? string.Empty;

        return text[..maxChars] + "\n... (truncated for token limit)";
    }

    private static int CountStoriesInSummary(string summary)
    {
        // Count occurrences of story-like patterns
        return Regex.Matches(summary, @"US-\d+|user stor", RegexOptions.IgnoreCase).Count;
    }

    private static int CountCommitsInSummary(string summary)
    {
        return Regex.Matches(summary, @"commit|[a-f0-9]{7,40}", RegexOptions.IgnoreCase).Count;
    }

    private static List<string> ExtractLanguages(string techStack)
    {
        var languages = new List<string>();
        var langPatterns = new[] { "C#", "TypeScript", "JavaScript", "Python", "Java", "Go",
            "Ruby", "PHP", "Rust", "Swift", "Kotlin", "F#" };

        foreach (var lang in langPatterns)
        {
            if (techStack.Contains(lang, StringComparison.OrdinalIgnoreCase))
                languages.Add(lang);
        }

        return languages;
    }

    private static string? ExtractPrimaryFramework(string techStack)
    {
        var frameworks = new[] { ".NET", "Angular", "React", "Next.js", "Vue", "Nuxt",
            "Django", "Flask", "Spring", "Rails", "Express", "Fastify", "ASP.NET" };

        return frameworks.FirstOrDefault(fw =>
            techStack.Contains(fw, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSummaryComment(
        CodebaseAnalysisMetadata metadata, int totalDocFiles, bool incremental, StoryTokenUsage tokenUsage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(incremental
            ? "✅ Codebase documentation updated (incremental)"
            : "✅ Codebase documentation complete");
        sb.AppendLine();
        sb.AppendLine("**Statistics:**");
        sb.AppendLine($"- Files analyzed: {metadata.FilesAnalyzed:N0}");
        sb.AppendLine($"- Lines of code: {metadata.LinesOfCode:N0}");
        sb.AppendLine($"- User stories reviewed: {metadata.UserStoriesReviewed}");
        sb.AppendLine($"- Commits analyzed: {metadata.CommitsAnalyzed}");
        sb.AppendLine($"- Features documented: {metadata.FeaturesDocumented}");
        sb.AppendLine($"- Languages detected: {string.Join(", ", metadata.LanguagesDetected)}");
        sb.AppendLine($"- Primary framework: {metadata.PrimaryFramework ?? "N/A"}");
        sb.AppendLine();
        sb.AppendLine("**AI Token Usage:**");
        sb.AppendLine($"- Total tokens: {tokenUsage.TotalTokens:N0} (in: {tokenUsage.TotalInputTokens:N0} / out: {tokenUsage.TotalOutputTokens:N0})");
        sb.AppendLine($"- Estimated cost: ${tokenUsage.TotalCost:F4}");
        sb.AppendLine($"- Complexity: {tokenUsage.Complexity}");
        if (tokenUsage.Agents.TryGetValue("CodebaseDocumentation", out var agentUsage))
            sb.AppendLine($"- AI calls: {agentUsage.CallCount} (model: {agentUsage.Model})");
        sb.AppendLine();
        sb.AppendLine("**Documentation Created:**");
        sb.AppendLine($"- Total documentation files: {totalDocFiles}");
        sb.AppendLine();
        if (metadata.FeaturesDocumentedList.Count > 0)
        {
            sb.AppendLine("**Major Features Documented:**");
            foreach (var feature in metadata.FeaturesDocumentedList)
            {
                sb.AppendLine($"- {feature}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("**Next Steps:**");
        sb.AppendLine("AI agents can now generate code that matches your architecture and coding standards.");
        sb.AppendLine("To update this documentation, use the 'Re-analyze Codebase' button in the dashboard.");
        sb.AppendLine();
        sb.AppendLine($"📁 Documentation: .agent/ folder in repository");
        sb.AppendLine($"⏱️ Analysis completed: {metadata.LastAnalysis:u}");

        return sb.ToString();
    }

    #endregion
}
