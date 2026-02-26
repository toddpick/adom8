using System.Text.Json;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Telemetry;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Queue trigger that dispatches agent tasks to the appropriate agent service.
/// Uses .NET 8 keyed DI to resolve the correct IAgentService implementation.
/// Enforces autonomy-level early exits: Level 1 stops after Planning, Level 2 stops after Testing.
/// Handles <see cref="AgentResult"/> from agents: retries transient errors, fails permanently on configuration/data errors.
/// </summary>
public sealed class AgentTaskDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentTaskDispatcher> _logger;
    private readonly IActivityLogger _activityLogger;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IRepositorySizingService _repositorySizingService;
    private readonly TelemetryClient _telemetry;
    private readonly ISaasCallbackService _saasCallback;

    public AgentTaskDispatcher(
        IServiceProvider serviceProvider,
        ILogger<AgentTaskDispatcher> logger,
        IActivityLogger activityLogger,
        IAzureDevOpsClient adoClient,
        IRepositorySizingService repositorySizingService,
        TelemetryClient telemetry,
        ISaasCallbackService saasCallback)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _activityLogger = activityLogger;
        _adoClient = adoClient;
        _repositorySizingService = repositorySizingService;
        _telemetry = telemetry;
        _saasCallback = saasCallback;
    }

    [Function("AgentTaskDispatcher")]
    public async Task Run(
        [QueueTrigger("agent-tasks", Connection = "AzureWebJobsStorage")] string messageText,
        CancellationToken cancellationToken)
    {
        AgentTask? agentTask;
        try
        {
            agentTask = JsonSerializer.Deserialize<AgentTask>(messageText);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize agent task message: {Message}", messageText);
            throw; // Let the queue infrastructure handle poison messages
        }

        if (agentTask is null)
        {
            _logger.LogError("Deserialized agent task is null");
            return;
        }

        try
        {
            await RunCoreAsync(agentTask, cancellationToken);
        }
        catch (Exception ex)
        {
            if (agentTask.WorkItemId <= 0)
            {
                throw;
            }

            var dispatcherFailure = AgentResult.Fail(
                ErrorCategory.Code,
                $"Dispatcher failed before completion: {ex.Message}",
                ex);

            _logger.LogError(
                ex,
                "Dispatcher exception for WI-{WorkItemId} ({AgentType})",
                agentTask.WorkItemId,
                agentTask.AgentType);

            await MarkWorkItemFailedAsync(agentTask, dispatcherFailure, cancellationToken);
        }
    }

    private async Task RunCoreAsync(AgentTask agentTask, CancellationToken cancellationToken)
    {

        using var scope = _serviceProvider.CreateScope();

        // Autonomy-level early exit: fetch work item to check autonomy level
        // Skip work item lookup for standalone agents (e.g., CodebaseDocumentation with WI-0)
        if (agentTask.WorkItemId > 0)
        {
            var workItem = await _adoClient.GetWorkItemAsync(agentTask.WorkItemId, cancellationToken);
            var autonomyLevel = workItem.AutonomyLevel;

            if (ShouldSkipAgent(autonomyLevel, agentTask.AgentType))
            {
                _logger.LogInformation(
                    "Skipping {AgentType} agent for WI-{WorkItemId}: autonomy level {Level} does not include this stage",
                    agentTask.AgentType, agentTask.WorkItemId, autonomyLevel);

                await _activityLogger.LogAsync(
                    agentTask.AgentType.ToString(),
                    agentTask.WorkItemId,
                    $"Skipped — autonomy level {autonomyLevel} stops before {agentTask.AgentType}",
                    cancellationToken: cancellationToken);

                return;
            }

            // State guard: verify the work item has progressed far enough for this agent.
            // This prevents stale/duplicate queue messages from triggering agents out of order
            // (e.g., if a previous run enqueued Testing but the story was reset to Planning).
            // We block if the WI is BEFORE the expected state (stale message), but allow if
            // it's at or past the expected state (normal flow or late-arriving message).
            var currentState = workItem.State;
            if (IsStateBehind(currentState, agentTask.AgentType))
            {
                _logger.LogWarning(
                    "Skipping {AgentType} agent for WI-{WorkItemId}: work item is in state '{CurrentState}' which is before the expected state — stale queue message",
                    agentTask.AgentType, agentTask.WorkItemId, currentState);

                await _activityLogger.LogAsync(
                    agentTask.AgentType.ToString(),
                    agentTask.WorkItemId,
                    $"Skipped — work item is in state '{currentState}' (too early for {agentTask.AgentType}). Stale queue message discarded.",
                    cancellationToken: cancellationToken);

                return;
            }
        }

        if (RequiresCloneCapacityCheck(agentTask.AgentType))
        {
            var sizing = await _repositorySizingService.EvaluateAsync(cancellationToken);
            if (sizing.CheckPerformed && !sizing.CanProceed)
            {
                var blockedMessage = $"{sizing.Message} Agent stage blocked: {agentTask.AgentType}.";

                await _activityLogger.LogAsync(
                    agentTask.AgentType.ToString(),
                    agentTask.WorkItemId,
                    blockedMessage,
                    "error",
                    cancellationToken);

                _telemetry.TrackEvent("RepositoryCapacityBlocked", new Dictionary<string, string>
                {
                    [TelemetryProperties.WorkItemId] = agentTask.WorkItemId.ToString(),
                    [TelemetryProperties.AgentType] = agentTask.AgentType.ToString(),
                    [TelemetryProperties.CorrelationId] = agentTask.CorrelationId,
                    ["capacityMessage"] = sizing.Message
                },
                new Dictionary<string, double>
                {
                    ["estimatedWorkingTreeBytes"] = sizing.EstimatedWorkingTreeBytes,
                    ["estimatedBinaryBytes"] = sizing.EstimatedBinaryBytes,
                    ["estimatedFileCount"] = sizing.FileCount
                });

                if (agentTask.WorkItemId > 0)
                {
                    var failResult = AgentResult.Fail(ErrorCategory.Configuration, blockedMessage);
                    await MarkWorkItemFailedAsync(agentTask, failResult, cancellationToken);
                }

                _logger.LogWarning(
                    "Blocked {AgentType} for WI-{WorkItemId} by repository capacity policy: {Reason}",
                    agentTask.AgentType,
                    agentTask.WorkItemId,
                    sizing.Message);

                return;
            }
        }

        _logger.LogInformation(
            "Dispatching {AgentType} agent for WI-{WorkItemId} (correlation: {CorrelationId})",
            agentTask.AgentType, agentTask.WorkItemId, agentTask.CorrelationId);

        var agentKey = agentTask.AgentType.ToString();

        IAgentService? agentService;
        try
        {
            agentService = scope.ServiceProvider.GetRequiredKeyedService<IAgentService>(agentKey);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "No agent service registered for key '{AgentKey}'", agentKey);
            throw;
        }

        await _activityLogger.LogAsync(
            agentKey,
            agentTask.WorkItemId,
            $"{agentKey} agent started processing",
            cancellationToken: cancellationToken);

        _telemetry.TrackEvent(TelemetryEvents.AgentStarted, new Dictionary<string, string>
        {
            [TelemetryProperties.WorkItemId] = agentTask.WorkItemId.ToString(),
            [TelemetryProperties.AgentType] = agentKey,
            [TelemetryProperties.CorrelationId] = agentTask.CorrelationId
        });

        // Fire-and-forget SaaS status update (no-op when SaaS mode is disabled)
        await _saasCallback.ReportAgentStartedAsync(agentTask.CorrelationId, agentKey, cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        AgentResult result;

        try
        {
            result = await agentService.ExecuteAsync(agentTask, cancellationToken);
        }
        catch (Exception ex)
        {
            // Agent threw instead of returning AgentResult — treat as Code error
            result = AgentResult.Fail(ErrorCategory.Code, $"Unhandled exception in {agentKey}: {ex.Message}", ex);
        }

        stopwatch.Stop();

        if (result.Success)
        {
            // When the Coding agent delegates to Copilot it returns Ok(0, 0m)
            // and logs its own "Delegated to @..." message. Skip the generic
            // "completed successfully" message so the activity feed isn't misleading.
            var isCopilotDelegation = agentTask.AgentType == AgentType.Coding
                                     && result.TokensUsed == 0
                                     && result.CostIncurred == 0m;

            var isPlanningNeedsRevision = false;
            if (agentTask.AgentType == AgentType.Planning)
            {
                try
                {
                    var planningWorkItem = await _adoClient.GetWorkItemAsync(agentTask.WorkItemId, cancellationToken);
                    if (string.Equals(planningWorkItem.State, "Needs Revision", StringComparison.OrdinalIgnoreCase))
                    {
                        isPlanningNeedsRevision = true;
                        await _activityLogger.LogAsync(
                            agentKey,
                            agentTask.WorkItemId,
                            "Planning triage rejected story — moved to Needs Revision. More information required.",
                            cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to evaluate Needs Revision status for WI-{WorkItemId}", agentTask.WorkItemId);
                }
            }

            if (!isCopilotDelegation && !isPlanningNeedsRevision)
            {
                await _activityLogger.LogAsync(
                    agentKey,
                    agentTask.WorkItemId,
                    $"{agentKey} agent completed successfully",
                    result.TokensUsed,
                    result.CostIncurred,
                    cancellationToken: cancellationToken);
            }

            _telemetry.TrackEvent(TelemetryEvents.AgentCompleted, new Dictionary<string, string>
            {
                [TelemetryProperties.WorkItemId] = agentTask.WorkItemId.ToString(),
                [TelemetryProperties.AgentType] = agentKey,
                [TelemetryProperties.CorrelationId] = agentTask.CorrelationId
            },
            new Dictionary<string, double>
            {
                [TelemetryProperties.Duration] = stopwatch.ElapsedMilliseconds
            });

            _logger.LogInformation(
                "{AgentType} agent completed for WI-{WorkItemId} in {Duration}ms",
                agentTask.AgentType, agentTask.WorkItemId, stopwatch.ElapsedMilliseconds);

            // SaaS callback — report success (no-op when SaaS mode is disabled)
            await _saasCallback.ReportAgentCompletedAsync(
                agentTask.CorrelationId,
                agentKey,
                inputTokens: 0,
                outputTokens: result.TokensUsed,
                durationMs: (int)stopwatch.ElapsedMilliseconds,
                cancellationToken: cancellationToken);
        }
        else
        {
            _telemetry.TrackEvent(TelemetryEvents.AgentFailed, new Dictionary<string, string>
            {
                [TelemetryProperties.WorkItemId] = agentTask.WorkItemId.ToString(),
                [TelemetryProperties.AgentType] = agentKey,
                [TelemetryProperties.CorrelationId] = agentTask.CorrelationId,
                [TelemetryProperties.ErrorCategory] = result.Category.ToString()!,
                [TelemetryProperties.ErrorMessage] = result.ErrorMessage ?? "Unknown error"
            });

            _logger.LogError(
                result.Exception,
                "{AgentType} agent failed for WI-{WorkItemId} ({ErrorCategory}): {ErrorMessage}",
                agentTask.AgentType, agentTask.WorkItemId, result.Category, result.ErrorMessage);

            await _activityLogger.LogAsync(
                agentKey,
                agentTask.WorkItemId,
                $"{agentKey} agent failed ({result.Category}): {result.ErrorMessage}",
                "error",
                cancellationToken);

            // SaaS callback — report failure (no-op when SaaS mode is disabled)
            await _saasCallback.ReportAgentFailedAsync(
                agentTask.CorrelationId,
                agentKey,
                result.ErrorMessage ?? "Unknown error",
                cancellationToken);

            // Decide retry vs. permanent failure based on error category
            switch (result.Category)
            {
                case ErrorCategory.Transient:
                    // For story-backed tasks, fail fast and surface the error immediately.
                    if (agentTask.WorkItemId > 0)
                    {
                        await MarkWorkItemFailedAsync(agentTask, result, cancellationToken);
                        break;
                    }

                    // Standalone tasks (e.g., WI-0) retain retry behavior.
                    throw result.Exception ?? new InvalidOperationException(result.ErrorMessage);

                case ErrorCategory.Configuration:
                case ErrorCategory.Data:
                    // Permanent failure — notify user and stop retry loop for story-backed tasks
                    if (agentTask.WorkItemId > 0)
                    {
                        await MarkWorkItemFailedAsync(agentTask, result, cancellationToken);
                    }
                    break; // Consume message — no retry

                case ErrorCategory.Code:
                default:
                    // For story-backed tasks, fail fast with visible ADO state/comment.
                    if (agentTask.WorkItemId > 0)
                    {
                        await MarkWorkItemFailedAsync(agentTask, result, cancellationToken);
                        break;
                    }

                    // Standalone tasks (e.g., WI-0) retain retry behavior.
                    throw result.Exception ?? new InvalidOperationException(result.ErrorMessage);
            }
        }
    }

    private async Task MarkWorkItemFailedAsync(AgentTask task, AgentResult result, CancellationToken ct)
    {
        await PostFailureCommentAsync(task, result, ct);

        try
        {
            await _adoClient.UpdateWorkItemFieldAsync(
                task.WorkItemId,
                CustomFieldNames.Paths.CurrentAIAgent,
                ToCurrentAgentValue(task.AgentType),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set Current AI Agent before fail-state for WI-{WorkItemId}", task.WorkItemId);
        }

        try
        {
            await _adoClient.UpdateWorkItemStateAsync(task.WorkItemId, "Agent Failed", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set Agent Failed state for WI-{WorkItemId}", task.WorkItemId);
        }

        _telemetry.TrackEvent(TelemetryEvents.AgentPermanentFailure, new Dictionary<string, string>
        {
            [TelemetryProperties.WorkItemId] = task.WorkItemId.ToString(),
            [TelemetryProperties.AgentType] = task.AgentType.ToString(),
            [TelemetryProperties.CorrelationId] = task.CorrelationId,
            [TelemetryProperties.ErrorCategory] = result.Category.ToString()!
        });
    }

    /// <summary>
    /// Posts a formatted failure comment to the Azure DevOps work item.
    /// </summary>
    private async Task PostFailureCommentAsync(AgentTask task, AgentResult result, CancellationToken ct)
    {
        var recommendation = result.Category switch
        {
            ErrorCategory.Configuration => "Check configuration settings (API keys, PATs, credentials).",
            ErrorCategory.Data => "Review work item content for formatting issues or invalid data.",
            _ => "Check Application Insights logs for details."
        };

        var comment = $"""
            ❌ Agent execution failed — permanent error (will not retry)

            **Failed Agent:** {task.AgentType}
            **Error Category:** {result.Category}
            **Error:** {result.ErrorMessage}

            **Recommended Action:** {recommendation}

            **Troubleshooting:**
            1. Check Application Insights with CorrelationId: {task.CorrelationId}
            2. See TROUBLESHOOTING.md for common solutions
            3. Fix the issue, then set work item state back to re-trigger
            """;

        try
        {
            await _adoClient.AddWorkItemCommentAsync(task.WorkItemId, comment, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post failure comment to WI-{WorkItemId}", task.WorkItemId);
        }
    }

    /// <summary>
    /// Determines if the given agent should be skipped based on the autonomy level.
    /// Level 1 (Plan Only): only Planning runs.
    /// Level 2 (Code Only): Planning + Coding run. Testing/Review/Docs/Deploy skipped.
    /// Levels 3-5: all agents run (Deployment agent handles the rest).
    /// CodebaseDocumentation always runs (it's triggered outside the normal pipeline).
    /// </summary>
    private static bool ShouldSkipAgent(int autonomyLevel, AgentType agentType) => agentType switch
    {
        AgentType.CodebaseDocumentation => false, // standalone, always runs
        _ => autonomyLevel switch
        {
            1 => agentType > AgentType.Planning,
            2 => agentType > AgentType.Coding,
            _ => false
        }
    };

    private static bool RequiresCloneCapacityCheck(AgentType agentType) => agentType switch
    {
        AgentType.Planning => true,
        AgentType.Coding => true,
        AgentType.Testing => true,
        AgentType.Review => true,
        AgentType.Documentation => true,
        AgentType.CodebaseDocumentation => false,
        _ => false
    };

    /// <summary>
    /// Returns true if the work item's current ADO state is BEFORE the stage where
    /// the given agent should run. This blocks stale queue messages from earlier runs
    /// from triggering agents prematurely (e.g., when a story is reset to "Story Planning"
    /// but Testing/Review messages from the previous run are still in the queue).
    /// 
    /// Only blocks when we are certain the WI has not reached the agent's stage yet:
    /// - "New" or "Story Planning" blocks everything except Planning
    /// - "AI Code" blocks everything except Planning and Coding
    /// - Other states allow agents to proceed (each agent validates its own preconditions)
    /// </summary>
    private static bool IsStateBehind(string currentState, AgentType agentType)
    {
        var stateOrder = GetStateOrder(currentState);
        var requiredOrder = GetMinimumStateOrder(agentType);
        // If we can't determine the order (unknown state), allow the agent to proceed
        if (stateOrder < 0 || requiredOrder < 0) return false;
        return stateOrder < requiredOrder;
    }

    private static int GetStateOrder(string? state) => state?.ToLowerInvariant() switch
    {
        "new" => 0,
        _ => -1 // Once the WI has entered the pipeline, don't block — each agent validates internally
    };

    private static int GetMinimumStateOrder(AgentType agentType) => agentType switch
    {
        AgentType.Planning => 0,   // Planning can run from "New"
        AgentType.Coding => 1,     // Requires past "New"
        AgentType.Testing => 1,
        AgentType.Review => 1,
        AgentType.Documentation => 1,
        AgentType.Deployment => 1,
        _ => -1 // CodebaseDocumentation — no requirement
    };

    private static string ToCurrentAgentValue(AgentType agentType) => agentType switch
    {
        AgentType.Planning => AIPipelineNames.CurrentAgentValues.Planning,
        AgentType.Coding => AIPipelineNames.CurrentAgentValues.Coding,
        AgentType.Testing => AIPipelineNames.CurrentAgentValues.Testing,
        AgentType.Review => AIPipelineNames.CurrentAgentValues.Review,
        AgentType.Documentation => AIPipelineNames.CurrentAgentValues.Documentation,
        AgentType.Deployment => AIPipelineNames.CurrentAgentValues.Deployment,
        _ => string.Empty
    };
}
