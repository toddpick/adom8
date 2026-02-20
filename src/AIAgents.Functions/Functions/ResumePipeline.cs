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
/// HTTP trigger that resumes the agent pipeline after human coding.
/// POST /api/resume — accepts { workItemId } and enqueues the Testing agent.
/// Called after a developer has completed coding on the feature branch.
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
        var branchName = $"feature/US-{workItemId}";

        _logger.LogInformation("Resume pipeline requested for WI-{WorkItemId}", workItemId);

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

        // Update ADO state + current AI agent
        try
        {
            await _adoClient.UpdateWorkItemStateAsync(workItemId, AIPipelineNames.ProcessingState, cancellationToken);
            await _adoClient.UpdateWorkItemFieldAsync(workItemId, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Testing, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update ADO state for WI-{WorkItemId}", workItemId);
            // Continue anyway — the pipeline will update state
        }

        // Log activity
        await _activityLogger.LogAsync("Coding", workItemId,
            "Human coding complete — resuming pipeline at Testing agent",
            cancellationToken: cancellationToken);

        // Enqueue Testing agent
        var task = new AgentTask
        {
            WorkItemId = workItemId,
            AgentType = AgentType.Testing,
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        await _taskQueue.EnqueueAsync(task, cancellationToken);

        _logger.LogInformation("Pipeline resumed for WI-{WorkItemId}, enqueued Testing agent", workItemId);

        return new OkObjectResult(new
        {
            status = "resumed",
            workItemId,
            nextAgent = "Testing",
            message = $"Pipeline resumed for US-{workItemId}. Testing agent enqueued."
        });
    }

    private sealed class ResumeRequest
    {
        public int WorkItemId { get; set; }
    }
}
