using System.Text.Json;
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

    public CodebaseIntelligence(
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IActivityLogger activityLogger,
        ILogger<CodebaseIntelligence> logger,
        IConfiguration configuration)
    {
        _adoClient = adoClient;
        _gitOps = gitOps;
        _activityLogger = activityLogger;
        _logger = logger;

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
}
