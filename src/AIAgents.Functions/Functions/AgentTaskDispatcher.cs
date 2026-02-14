using System.Text.Json;
using AIAgents.Core.Interfaces;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Queue trigger that dispatches agent tasks to the appropriate agent service.
/// Uses .NET 8 keyed DI to resolve the correct IAgentService implementation.
/// Enforces autonomy-level early exits: Level 1 stops after Planning, Level 2 stops after Testing.
/// </summary>
public sealed class AgentTaskDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentTaskDispatcher> _logger;
    private readonly IActivityLogger _activityLogger;
    private readonly IAzureDevOpsClient _adoClient;

    public AgentTaskDispatcher(
        IServiceProvider serviceProvider,
        ILogger<AgentTaskDispatcher> logger,
        IActivityLogger activityLogger,
        IAzureDevOpsClient adoClient)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _activityLogger = activityLogger;
        _adoClient = adoClient;
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

        try
        {
            await agentService.ExecuteAsync(agentTask, cancellationToken);

            await _activityLogger.LogAsync(
                agentKey,
                agentTask.WorkItemId,
                $"{agentKey} agent completed successfully",
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "{AgentType} agent completed for WI-{WorkItemId}",
                agentTask.AgentType, agentTask.WorkItemId);
        }
        catch (Exception ex)
        {
            await _activityLogger.LogAsync(
                agentKey,
                agentTask.WorkItemId,
                $"{agentKey} agent failed: {ex.Message}",
                "error",
                cancellationToken);

            _logger.LogError(ex,
                "{AgentType} agent failed for WI-{WorkItemId}",
                agentTask.AgentType, agentTask.WorkItemId);

            throw; // Re-throw to trigger queue retry
        }
    }

    /// <summary>
    /// Determines if the given agent should be skipped based on the autonomy level.
    /// Level 1 (Plan Only): only Planning runs.
    /// Level 2 (Code Only): Planning, Coding, Testing run.
    /// Levels 3-5: all agents run (Deployment agent handles the rest).
    /// CodebaseDocumentation always runs (it's triggered outside the normal pipeline).
    /// </summary>
    private static bool ShouldSkipAgent(int autonomyLevel, AgentType agentType) => agentType switch
    {
        AgentType.CodebaseDocumentation => false, // standalone, always runs
        _ => autonomyLevel switch
        {
            1 => agentType > AgentType.Planning,
            2 => agentType > AgentType.Testing,
            _ => false
        }
    };
}
