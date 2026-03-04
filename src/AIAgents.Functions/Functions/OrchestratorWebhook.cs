using System.Text.Json;
using AIAgents.Core.Constants;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Core.Telemetry;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// HTTP trigger that receives Azure DevOps Service Hook webhooks.
/// Routes work item state changes into the agent pipeline by enqueuing AgentTask messages.
/// Validates input before queuing to prevent prompt injection, malicious content, and excessive costs.
/// </summary>
public sealed class OrchestratorWebhook
{
    private readonly ILogger<OrchestratorWebhook> _logger;
    private readonly IActivityLogger _activityLogger;
    private readonly IAgentTaskQueue _taskQueue;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IInputValidator _inputValidator;
    private readonly TelemetryClient _telemetry;
    private readonly CopilotOptions _copilotOptions;
    private readonly IGitHubOrchestrationLauncherService _orchestrationLauncher;

    // Maps ADO work item states to the agent that processes them.
    // ONLY "AI Agent" is here — all other transitions are handled by direct
    // EnqueueAsync calls within each agent. This prevents double-dispatch where
    // both the webhook AND the agent enqueue the next step, causing exponential reruns.
    private static readonly Dictionary<string, AgentType> s_stateToAgent = new(StringComparer.OrdinalIgnoreCase)
    {
        [AIPipelineNames.ProcessingState] = AgentType.Planning
    };

    private static readonly Dictionary<string, AgentType> s_currentAgentToAgentType = new(StringComparer.OrdinalIgnoreCase)
    {
        [AIPipelineNames.CurrentAgentValues.Planning] = AgentType.Planning,
        [AIPipelineNames.CurrentAgentValues.Coding] = AgentType.Coding,
        [AIPipelineNames.CurrentAgentValues.Testing] = AgentType.Testing,
        [AIPipelineNames.CurrentAgentValues.Review] = AgentType.Review,
        [AIPipelineNames.CurrentAgentValues.Documentation] = AgentType.Documentation,
        [AIPipelineNames.CurrentAgentValues.Deployment] = AgentType.Deployment,
        ["Deploy Agent"] = AgentType.Deployment
    };

    public OrchestratorWebhook(
        ILogger<OrchestratorWebhook> logger,
        IActivityLogger activityLogger,
        IAgentTaskQueue taskQueue,
        IAzureDevOpsClient adoClient,
        IInputValidator inputValidator,
        TelemetryClient telemetry,
        IOptions<CopilotOptions> copilotOptions,
        IGitHubOrchestrationLauncherService orchestrationLauncher)
    {
        _logger = logger;
        _activityLogger = activityLogger;
        _taskQueue = taskQueue;
        _adoClient = adoClient;
        _inputValidator = inputValidator;
        _telemetry = telemetry;
        _copilotOptions = copilotOptions.Value;
        _orchestrationLauncher = orchestrationLauncher;
    }

    [Function("OrchestratorWebhook")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "webhook")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received webhook request");

        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new BadRequestObjectResult(new { error = "Empty request body" });
        }

        ServiceHookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ServiceHookPayload>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse webhook payload");
            return new BadRequestObjectResult(new { error = "Invalid JSON payload" });
        }

        if (payload?.Resource is null)
        {
            return new BadRequestObjectResult(new { error = "Missing resource in payload" });
        }

        // Get the work item ID from wherever it exists in the payload
        var workItemId = payload.Resource.WorkItemId > 0
            ? payload.Resource.WorkItemId
            : payload.Resource.Revision?.Id ?? payload.Resource.Id;

        if (workItemId <= 0)
        {
            return new BadRequestObjectResult(new { error = "Could not determine work item ID" });
        }

        // Determine the new state — ONLY from an explicit state change, never the current state.
        // Reading current state from revision fields caused infinite loops: any ADO update
        // (comment, field change) while WI was in a mapped state would re-trigger the agent.
        var stateChange = payload.Resource.Fields?.State;
        var newState = stateChange?.NewValue;
        var oldState = stateChange?.OldValue;

        if (_copilotOptions.Enabled &&
            (string.Equals(newState, "Needs Revision", StringComparison.OrdinalIgnoreCase)
             || string.Equals(newState, "Agent Failed", StringComparison.OrdinalIgnoreCase)))
        {
            await _orchestrationLauncher.CleanupForStoryStateAsync(workItemId, newState!, cancellationToken);

            await _activityLogger.LogAsync(
                "Orchestrator",
                workItemId,
                $"Closed delegated GitHub artifacts due to state transition to '{newState}'",
                cancellationToken: cancellationToken);

            return new OkObjectResult(new
            {
                status = "cleaned-up",
                workItemId,
                state = newState
            });
        }

        if (string.IsNullOrEmpty(newState) || !s_stateToAgent.TryGetValue(newState, out var agentType))
        {
            _logger.LogInformation(
                "State '{NewState}' for WI-{WorkItemId} does not map to any agent, skipping",
                newState, workItemId);
            return new OkObjectResult(new { status = "skipped", reason = $"State '{newState}' is not an agent trigger" });
        }

        // Validate work item content before queuing
        string? currentAIAgentValue = null;
        StoryWorkItem? workItem = null;
        try
        {
            workItem = await _adoClient.GetWorkItemAsync(workItemId, cancellationToken);
            currentAIAgentValue = workItem.CurrentAIAgent;
            var validation = _inputValidator.ValidateWorkItem(workItem);

            if (!validation.IsValid)
            {
                _telemetry.TrackEvent(TelemetryEvents.InputValidationFailed, new Dictionary<string, string>
                {
                    [TelemetryProperties.WorkItemId] = workItemId.ToString(),
                    [TelemetryProperties.ErrorMessage] = string.Join("; ", validation.Errors)
                });

                var errorComment = $"⚠️ **Input Validation Failed** — work item will not be processed.\n\n" +
                    string.Join("\n", validation.Errors.Select(e => $"- {e}"));

                await _adoClient.AddWorkItemCommentAsync(workItemId, errorComment, cancellationToken);

                _logger.LogWarning(
                    "Input validation failed for WI-{WorkItemId}: {Errors}",
                    workItemId, string.Join("; ", validation.Errors));

                return new OkObjectResult(new
                {
                    status = "validation_failed",
                    workItemId,
                    errors = validation.Errors
                });
            }

            if (validation.Warnings.Count > 0)
            {
                _telemetry.TrackEvent(TelemetryEvents.InputValidationWarning, new Dictionary<string, string>
                {
                    [TelemetryProperties.WorkItemId] = workItemId.ToString(),
                    [TelemetryProperties.ErrorMessage] = string.Join("; ", validation.Warnings)
                });

                var warningComment = $"⚠️ **Input Validation Warnings** — processing will continue.\n\n" +
                    string.Join("\n", validation.Warnings.Select(w => $"- {w}"));

                await _adoClient.AddWorkItemCommentAsync(workItemId, warningComment, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // If we can't fetch the work item for validation, log but still proceed
            _logger.LogWarning(ex,
                "Could not validate WI-{WorkItemId} content — proceeding without validation",
                workItemId);
        }

        var shouldResumeFromCurrentAgent =
            agentType == AgentType.Planning &&
            string.Equals(oldState, AIPipelineNames.ProcessingState, StringComparison.OrdinalIgnoreCase);

        if (shouldResumeFromCurrentAgent &&
            !string.IsNullOrWhiteSpace(currentAIAgentValue) &&
            s_currentAgentToAgentType.TryGetValue(currentAIAgentValue.Trim(), out var requestedAgentType))
        {
            agentType = requestedAgentType;
            _logger.LogInformation(
                "AI Agent trigger for WI-{WorkItemId} is resuming at {AgentType} from Current AI Agent='{CurrentAIAgent}'",
                workItemId,
                agentType,
                currentAIAgentValue);
        }
        else if (agentType == AgentType.Planning && !string.IsNullOrWhiteSpace(currentAIAgentValue))
        {
            _logger.LogInformation(
                "Ignoring stale Current AI Agent='{CurrentAIAgent}' for WI-{WorkItemId} because state transitioned from '{OldState}' to '{NewState}'",
                currentAIAgentValue,
                workItemId,
                oldState ?? "(unknown)",
                newState ?? "(unknown)");
        }

        if (_copilotOptions.Enabled)
        {
            if (agentType == AgentType.Planning && !shouldResumeFromCurrentAgent)
            {
                try
                {
                    await _adoClient.UpdateWorkItemFieldAsync(
                        workItemId,
                        CustomFieldNames.Paths.CurrentAIAgent,
                        AIPipelineNames.CurrentAgentValues.Planning,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to seed Current AI Agent=Planning for WI-{WorkItemId} before GitHub kickoff",
                        workItemId);
                }

                var kickoffResult = await _orchestrationLauncher.KickoffAsync(
                    workItem ?? await _adoClient.GetWorkItemAsync(workItemId, cancellationToken),
                    Guid.NewGuid().ToString("N"),
                    forceNewIssue: true,
                    cancellationToken);

                await _adoClient.AddWorkItemCommentAsync(
                    workItemId,
                    kickoffResult.ExistingDelegation
                        ? $"<b>🤖 AI Orchestration</b><br/>Existing GitHub orchestration is already pending on Issue #{kickoffResult.IssueNumber}."
                        : $"<b>🤖 AI Orchestration Delegated</b><br/>Created GitHub Issue #{kickoffResult.IssueNumber} and assigned @{kickoffResult.AgentAssignee}.<br/>GitHub agent now owns Planning through Documentation and will update ADO via MCP.",
                    cancellationToken);

                await _activityLogger.LogAsync(
                    "Orchestrator",
                    workItemId,
                    kickoffResult.ExistingDelegation
                        ? $"Reused existing GitHub orchestration issue #{kickoffResult.IssueNumber}"
                        : $"Delegated full pipeline kickoff to GitHub issue #{kickoffResult.IssueNumber}",
                    cancellationToken: cancellationToken);

                return new OkObjectResult(new
                {
                    status = "delegated",
                    workItemId,
                    agent = "GitHub",
                    issueNumber = kickoffResult.IssueNumber
                });
            }

            if (shouldResumeFromCurrentAgent)
            {
                if (agentType == AgentType.Deployment)
                {
                    var currentWorkItem = workItem ?? await _adoClient.GetWorkItemAsync(workItemId, cancellationToken);
                    if (currentWorkItem.AutonomyLevel < 5)
                    {
                        await _activityLogger.LogAsync(
                            "Orchestrator",
                            workItemId,
                            "Skipped Deployment enqueue: full GitHub mode only allows Azure deployment execution at autonomy level 5.",
                            cancellationToken: cancellationToken);

                        _logger.LogInformation(
                            "Skipping Deployment enqueue for WI-{WorkItemId}: autonomy level {AutonomyLevel} < 5",
                            workItemId,
                            currentWorkItem.AutonomyLevel);

                        return new OkObjectResult(new
                        {
                            status = "skipped",
                            reason = "Deployment only runs in Azure for autonomy level 5",
                            workItemId
                        });
                    }
                }
                else
                {
                    await _activityLogger.LogAsync(
                        "Orchestrator",
                        workItemId,
                        $"Observed MCP stage transition to {agentType} in full GitHub mode — no local enqueue required.",
                        cancellationToken: cancellationToken);

                    _logger.LogInformation(
                        "Skipping local enqueue for WI-{WorkItemId} at {AgentType}: full GitHub mode owns Planning through Documentation",
                        workItemId,
                        agentType);

                    return new OkObjectResult(new
                    {
                        status = "observed",
                        workItemId,
                        stage = agentType.ToString()
                    });
                }
            }
        }

        if (agentType == AgentType.Planning)
        {
            try
            {
                await _adoClient.UpdateWorkItemFieldAsync(
                    workItemId,
                    CustomFieldNames.Paths.CurrentAIAgent,
                    AIPipelineNames.CurrentAgentValues.Planning,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to seed Current AI Agent=Planning for WI-{WorkItemId}",
                    workItemId);
            }

            _logger.LogInformation(
                "AI Agent trigger for WI-{WorkItemId} seeded Planning visibility and will enqueue Planning",
                workItemId);
        }

        // Enqueue the agent task
        var agentTask = new AgentTask
        {
            WorkItemId = workItemId,
            AgentType = agentType,
            TriggerSource = nameof(OrchestratorWebhook),
            ResumeFromStage = newState,
            HandoffNote = $"State transition webhook: {newState}"
        };

        await _taskQueue.EnqueueAsync(agentTask, cancellationToken);

        await _activityLogger.LogAsync(
            "Orchestrator",
            workItemId,
            $"Enqueued {agentType} agent for state '{newState}'",
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Enqueued {AgentType} task for WI-{WorkItemId} (state: {NewState})",
            agentType, workItemId, newState);

        return new OkObjectResult(new
        {
            status = "queued",
            workItemId,
            agent = agentType.ToString(),
            correlationId = agentTask.CorrelationId
        });
    }
}
