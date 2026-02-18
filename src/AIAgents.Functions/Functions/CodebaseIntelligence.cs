using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// HTTP triggers for the Codebase Intelligence feature.
/// POST /api/analyze-codebase — kicks off analysis (creates work item + enqueues agent).
/// GET  /api/codebase-intelligence — returns last analysis status + stats.
/// </summary>
public sealed class CodebaseIntelligence
{
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IActivityLogger _activityLogger;
    private readonly ILogger<CodebaseIntelligence> _logger;
    private readonly QueueClient _queueClient;
    private readonly CopilotOptions _copilotOptions;

    public CodebaseIntelligence(
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IActivityLogger activityLogger,
        ILogger<CodebaseIntelligence> logger,
        IConfiguration configuration,
        IOptions<CopilotOptions> copilotOptions)
    {
        _adoClient = adoClient;
        _gitOps = gitOps;
        _activityLogger = activityLogger;
        _logger = logger;
        _copilotOptions = copilotOptions.Value;

        var connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is required.");
        _queueClient = new QueueClient(connectionString, "agent-tasks");
        _queueClient.CreateIfNotExists();
    }

    /// <summary>
    /// POST /api/analyze-codebase
    /// Triggers a codebase documentation analysis by enqueuing a CodebaseDocumentation agent task.
    /// The dashboard calls this when the user clicks "Document My Codebase" or "Re-analyze".
    /// </summary>
    [Function("AnalyzeCodebase")]
    public async Task<IActionResult> AnalyzeCodebase(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "analyze-codebase")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received analyze-codebase request");

        AnalyzeCodebaseRequest? request;
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            request = string.IsNullOrWhiteSpace(body)
                ? new AnalyzeCodebaseRequest()
                : JsonSerializer.Deserialize<AnalyzeCodebaseRequest>(body);
        }
        catch (JsonException)
        {
            request = new AnalyzeCodebaseRequest();
        }

        request ??= new AnalyzeCodebaseRequest();

        // Build a description that carries configuration to the agent
        var description = BuildAnalysisDescription(request);
        var estimatedDuration = request.AnalysisDepth == "deep" ? "20-30 minutes" : "10-15 minutes";

        // Enqueue the CodebaseDocumentation agent task
        // We use WorkItemId = 0 as a sentinel for "no work item" —
        // the agent will create its own tracking via activity logger
        var agentTask = new AgentTask
        {
            WorkItemId = 0, // Will be replaced if ADO epic creation is enabled
            AgentType = AgentType.CodebaseDocumentation
        };

        // Try to create an ADO work item for tracking (best-effort)
        try
        {
            // For now, we enqueue with WI 0; the task description is in the activity log.
            // A future enhancement will create an Epic in ADO and use its ID.
            _logger.LogInformation("Enqueuing CodebaseDocumentation agent task");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create ADO work item for analysis tracking");
        }

        var messageJson = JsonSerializer.Serialize(agentTask);
        var base64Message = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
        await _queueClient.SendMessageAsync(base64Message, cancellationToken);

        await _activityLogger.LogAsync(
            "CodebaseDocumentation",
            0,
            $"Analysis queued: timeframe={request.UserStoryTimeframe}, depth={request.AnalysisDepth}, incremental={request.Incremental}",
            cancellationToken: cancellationToken);

        _logger.LogInformation("CodebaseDocumentation agent task enqueued (correlationId: {Id})", agentTask.CorrelationId);

        return new OkObjectResult(new AnalyzeCodebaseResponse
        {
            WorkItemId = agentTask.WorkItemId,
            EstimatedDuration = estimatedDuration,
            Status = "queued"
        });
    }

    /// <summary>
    /// GET /api/codebase-intelligence
    /// Returns the last analysis metadata and recommendation for re-analysis.
    /// The dashboard polls this to show codebase intelligence status.
    /// </summary>
    [Function("GetCodebaseIntelligence")]
    public async Task<IActionResult> GetCodebaseIntelligence(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "codebase-intelligence")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Codebase intelligence status request");

        CodebaseAnalysisMetadata? metadata = null;

        try
        {
            // Try to read metadata from the repo's .agent/metadata.json
            var repoPath = await _gitOps.EnsureBranchAsync("main", cancellationToken);
            var metadataContent = await _gitOps.ReadFileAsync(
                repoPath, ".agent/metadata.json", cancellationToken);

            if (!string.IsNullOrWhiteSpace(metadataContent))
            {
                metadata = JsonSerializer.Deserialize<CodebaseAnalysisMetadata>(metadataContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read codebase metadata from repo");
        }

        var response = new CodebaseIntelligenceResponse
        {
            LastAnalysis = metadata?.LastAnalysis,
            Status = metadata is null ? "not_analyzed" : "up_to_date",
            Stats = metadata,
            RecommendReanalysis = ShouldRecommendReanalysis(metadata)
        };

        return new OkObjectResult(response);
    }

    /// <summary>
    /// POST /api/initialize-codebase
    /// Creates an ADO User Story with detailed codebase scanning instructions
    /// and sets it to "Story Planning" so it flows through the AI pipeline.
    /// When Copilot is enabled, GitHub Copilot Coding Agent handles the heavy lifting.
    /// When not, the built-in agentic loop processes it via API.
    /// </summary>
    [Function("InitializeCodebase")]
    public async Task<IActionResult> InitializeCodebase(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "initialize-codebase")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received initialize-codebase request (Copilot enabled: {CopilotEnabled})", _copilotOptions.Enabled);

        if (_copilotOptions.Enabled)
        {
            // Copilot path: create an ADO story that flows through the pipeline → Copilot does the work
            var title = "Initialize Codebase Intelligence Documentation";
            var description = BuildCodebaseScanStoryDescription();

            var workItemId = await _adoClient.CreateWorkItemAsync(
                title, description, "Story Planning", cancellationToken);

            await _activityLogger.LogAsync(
                "CodebaseDocumentation",
                workItemId,
                "Codebase scan story created and set to Story Planning (Copilot path)",
                cancellationToken: cancellationToken);

            _logger.LogInformation("Created codebase scan story {WorkItemId} for Copilot path", workItemId);

            return new OkObjectResult(new { workItemId, status = "created", path = "copilot" });
        }
        else
        {
            // Non-Copilot path: use existing CodebaseDocumentationAgentService directly
            var agentTask = new AgentTask
            {
                WorkItemId = 0,
                AgentType = AgentType.CodebaseDocumentation
            };

            var messageJson = JsonSerializer.Serialize(agentTask);
            var base64Message = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
            await _queueClient.SendMessageAsync(base64Message, cancellationToken);

            await _activityLogger.LogAsync(
                "CodebaseDocumentation",
                0,
                "Codebase scan triggered via direct agent (non-Copilot path)",
                cancellationToken: cancellationToken);

            _logger.LogInformation("CodebaseDocumentation agent task enqueued (direct path)");

            return new OkObjectResult(new { workItemId = 0, status = "queued", path = "direct" });
        }
    }

    private static bool ShouldRecommendReanalysis(CodebaseAnalysisMetadata? metadata)
    {
        if (metadata?.LastAnalysis is null) return true;
        return (DateTime.UtcNow - metadata.LastAnalysis.Value).TotalDays > 30;
    }

    private static string BuildAnalysisDescription(AnalyzeCodebaseRequest request)
    {
        var parts = new List<string>
        {
            $"timeframe={request.UserStoryTimeframe}",
            $"depth={request.AnalysisDepth}"
        };

        if (!request.IncludeGitHistory)
            parts.Add("includeGitHistory=false");
        if (request.Incremental)
            parts.Add("incremental=true");

        return $"AI Codebase Documentation Analysis [{string.Join(", ", parts)}]";
    }

    /// <summary>
    /// Builds a comprehensive user story description that instructs the AI coding agent
    /// to scan the codebase and generate all .agent/ documentation files.
    /// Mirrors the work done by CodebaseDocumentationAgentService.
    /// </summary>
    private static string BuildCodebaseScanStoryDescription()
    {
        return """
<h2>Codebase Intelligence: Initial Documentation Scan</h2>

<h3>Overview</h3>
<p>Scan the entire repository and generate comprehensive AI-optimized documentation in the <code>.agent/</code> folder. 
This documentation will be used by all subsequent AI agents to understand the codebase structure, patterns, and conventions.</p>

<h3>What to Create</h3>
<p>Create a <code>.agent/</code> folder at the repository root containing the following files:</p>

<h4>Core Documentation Files</h4>
<ol>
<li><strong>CONTEXT_INDEX.md</strong> — Master overview of the project: purpose, high-level architecture, directory structure, 
key entry points, main features, quick reference for common tasks. This is the first file AI agents read.</li>

<li><strong>TECH_STACK.md</strong> — Languages, frameworks, versions, package managers, build tools, runtime requirements, 
key dependencies and their purposes. Include exact version numbers from project files.</li>

<li><strong>ARCHITECTURE.md</strong> — Architecture pattern (MVC, Clean Architecture, etc.), component relationships, 
data flow diagrams using Mermaid syntax, layer responsibilities, key design decisions. 
Include at least one Mermaid diagram showing the high-level system architecture.</li>

<li><strong>CODING_STANDARDS.md</strong> — Naming conventions (extracted from actual code patterns), file organization, 
error handling patterns, logging approach, dependency injection patterns, code formatting standards. 
Base these on the ACTUAL patterns found in the code, not generic best practices.</li>

<li><strong>COMMON_PATTERNS.md</strong> — Step-by-step how-to guides: how to add a new feature, add an API endpoint, 
add a UI component, add a database migration, write tests. Include specific file paths and code examples 
from the actual codebase.</li>

<li><strong>TESTING_STRATEGY.md</strong> — Test framework(s) used, test naming conventions, test organization, 
how to run tests, mocking patterns, coverage approach, integration vs unit test boundaries.</li>

<li><strong>DEPLOYMENT.md</strong> — Build process, CI/CD pipeline structure, infrastructure (Terraform, ARM, etc.), 
deployment steps, environment configuration, secrets management approach.</li>
</ol>

<h4>Conditional Documentation Files</h4>
<ol>
<li><strong>API_REFERENCE.md</strong> — (Create if the project has API endpoints) All endpoints with routes, 
HTTP methods, request/response formats, authentication requirements, error codes.</li>

<li><strong>DATABASE_SCHEMA.md</strong> — (Create if the project has a database) Tables/collections, relationships, 
ORM patterns, migration approach, connection management.</li>
</ol>

<h4>Feature Documentation</h4>
<p>Create a <code>.agent/FEATURES/</code> subfolder. For each major feature area detected in the codebase, 
create a separate markdown file (e.g., <code>authentication.md</code>, <code>data-access.md</code>, <code>notifications.md</code>).</p>
<p>Each feature file should contain: overview, key files involved, architecture/data flow (with Mermaid diagrams), 
configuration requirements, how to modify/extend it, testing approach for that feature.</p>
<p>Detect features by examining: folder structure, service/controller names, keyword patterns in code 
(auth, payment, notification, search, admin, reporting, etc.).</p>

<h4>Metadata Files</h4>
<ol>
<li><strong>metadata.json</strong> — JSON file with analysis stats:
<pre>{
  "lastAnalysis": "ISO-8601 timestamp",
  "filesAnalyzed": number,
  "linesOfCode": number,
  "featuresDocumented": number,
  "languagesDetected": ["lang1", "lang2"],
  "primaryFramework": "framework name",
  "documentationSizeKB": number,
  "featuresDocumentedList": ["feature1", "feature2"]
}</pre></li>

<li><strong>README.md</strong> — Human-readable guide explaining what the .agent/ folder is, 
why it exists, and how AI agents use it. Include analysis statistics.</li>
</ol>

<h3>How to Scan</h3>
<ol>
<li>Map the complete file/folder tree (exclude .git, node_modules, bin, obj, dist, build, vendor, 
__pycache__, .vs, .idea, packages, and other build output directories).</li>
<li>Detect the tech stack from project files (.csproj, package.json, requirements.txt, go.mod, Cargo.toml, 
pom.xml, build.gradle, Gemfile, etc.).</li>
<li>Sample 30-50 key source files (prioritize: entry points, controllers, services, repositories, models, 
configuration files, tests, middleware). Read enough of each file to understand patterns.</li>
<li>Detect coding patterns: naming conventions, error handling, logging, DI registration, 
file organization, testing approaches.</li>
<li>Identify features from folder names, class names, and code keywords.</li>
<li>Generate all documentation files with specific file paths, code examples, and Mermaid diagrams 
based on the ACTUAL code — not generic templates.</li>
</ol>

<h3>Important Guidelines</h3>
<ul>
<li>All documentation must reference ACTUAL file paths and code patterns from this specific repository.</li>
<li>Include Mermaid diagrams in ARCHITECTURE.md and feature docs showing real component relationships.</li>
<li>CODING_STANDARDS.md must be extracted from observed patterns, not generic guidelines.</li>
<li>COMMON_PATTERNS.md must include real file paths for "how to add X" guides.</li>
<li>Commit all files to the <code>.agent/</code> folder on the main branch.</li>
<li>Do NOT modify any existing source code — only create files in <code>.agent/</code>.</li>
</ul>

<h3>Acceptance Criteria</h3>
<ul>
<li>[ ] .agent/ folder exists at repository root with all core documentation files</li>
<li>[ ] CONTEXT_INDEX.md provides accurate project overview with real structure</li>
<li>[ ] ARCHITECTURE.md contains at least one Mermaid diagram of system architecture</li>
<li>[ ] CODING_STANDARDS.md reflects actual code conventions (not generic)</li>
<li>[ ] COMMON_PATTERNS.md has step-by-step guides with real file paths</li>
<li>[ ] FEATURES/ subfolder has per-feature documentation for detected features</li>
<li>[ ] metadata.json has accurate analysis statistics</li>
<li>[ ] No existing source code was modified</li>
</ul>
""";
    }
}
