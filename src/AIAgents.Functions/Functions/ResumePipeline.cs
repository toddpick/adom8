using System.Text.Json;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// HTTP trigger that resumes the agent pipeline.
/// POST /api/resume — accepts { workItemId, stage? }.
/// - stage omitted/default: resumes at Testing (legacy behavior)
/// - stage supported values: Testing, Review, Documentation, Deployment
/// </summary>
public sealed class ResumePipeline
{
    private readonly ILogger<ResumePipeline> _logger;
    private readonly IActivityLogger _activityLogger;
    private readonly IAgentTaskQueue _taskQueue;
    private readonly IGitOperations _gitOps;
    private readonly IAzureDevOpsClient _adoClient;

    public ResumePipeline(
        ILogger<ResumePipeline> logger,
        IActivityLogger activityLogger,
        IAgentTaskQueue taskQueue,
        IGitOperations gitOps,
        IAzureDevOpsClient adoClient)
    {
        _logger = logger;
        _activityLogger = activityLogger;
        _taskQueue = taskQueue;
        _gitOps = gitOps;
        _adoClient = adoClient;
    }

    [Function("ResumePipeline")]
    public async Task<IActionResult> Execute(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "resume")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        ResumeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ResumeRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Invalid JSON" });
        }

        if (request is null || request.WorkItemId <= 0)
        {
            return new BadRequestObjectResult(new { error = "workItemId is required" });
        }

        var workItemId = request.WorkItemId;
        var resumeTarget = ResolveResumeTarget(request.Stage);
        if (resumeTarget is null)
        {
            return new BadRequestObjectResult(new
            {
                error = "Invalid stage. Supported values: Testing, Review, Documentation, Deployment."
            });
        }

        var branchName = $"feature/US-{workItemId}";

        _logger.LogInformation(
            "Resume pipeline requested for WI-{WorkItemId} at stage {Stage}",
            workItemId,
            resumeTarget.Stage);

        // Verify the branch exists by attempting to ensure it
        try
        {
            await _gitOps.EnsureBranchAsync(branchName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Branch {Branch} not found for WI-{WorkItemId}", branchName, workItemId);
            return new NotFoundObjectResult(new { error = $"Branch '{branchName}' not found. Push code before resuming." });
        }

        // Update current AI agent only (state transitions are user-controlled in ADO)
        try
        {
            await _adoClient.UpdateWorkItemFieldAsync(
                workItemId,
                CustomFieldNames.Paths.CurrentAIAgent,
                resumeTarget.CurrentAgentValue,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update Current AI Agent field for WI-{WorkItemId}", workItemId);
            // Continue anyway — the pipeline will update state
        }

        // Log activity
        await _activityLogger.LogAsync(
            resumeTarget.ActivityAgent,
            workItemId,
            $"Pipeline resumed at {resumeTarget.Stage} agent",
            cancellationToken: cancellationToken);

        // Enqueue selected agent stage
        var task = new AgentTask
        {
            WorkItemId = workItemId,
            AgentType = resumeTarget.AgentType,
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        await _taskQueue.EnqueueAsync(task, cancellationToken);

        _logger.LogInformation(
            "Pipeline resumed for WI-{WorkItemId}, enqueued {AgentType} agent",
            workItemId,
            resumeTarget.AgentType);

        return new OkObjectResult(new
        {
            status = "resumed",
            workItemId,
            nextAgent = resumeTarget.Stage,
            message = $"Pipeline resumed for US-{workItemId}. {resumeTarget.Stage} agent enqueued."
        });
    }

    private static ResumeTarget? ResolveResumeTarget(string? stage) => stage?.Trim().ToLowerInvariant() switch
    {
        null or "" or "testing" => new ResumeTarget("Testing", AgentType.Testing, AIPipelineNames.CurrentAgentValues.Testing, "Testing"),
        "review" => new ResumeTarget("Review", AgentType.Review, AIPipelineNames.CurrentAgentValues.Review, "Review"),
        "documentation" or "docs" => new ResumeTarget("Documentation", AgentType.Documentation, AIPipelineNames.CurrentAgentValues.Documentation, "Documentation"),
        "deployment" or "deploy" => new ResumeTarget("Deployment", AgentType.Deployment, AIPipelineNames.CurrentAgentValues.Deployment, "Deployment"),
        _ => null
    };

    private sealed class ResumeRequest
    {
        public int WorkItemId { get; set; }
        public string? Stage { get; set; }
    }

    private sealed record ResumeTarget(string Stage, AgentType AgentType, string CurrentAgentValue, string ActivityAgent);
}
