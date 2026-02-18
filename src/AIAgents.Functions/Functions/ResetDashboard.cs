using AIAgents.Functions.Services;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Dashboard reset endpoint — clears all activity log entries and queues.
/// POST /api/reset — wipes the AgentActivity table and clears queues.
/// This gives users a clean slate on the dashboard without redeploying.
/// </summary>
public sealed class ResetDashboard
{
    private readonly IActivityLogger _activityLogger;
    private readonly QueueClient _taskQueue;
    private readonly QueueClient _poisonQueue;
    private readonly ILogger<ResetDashboard> _logger;

    public ResetDashboard(
        IActivityLogger activityLogger,
        IConfiguration configuration,
        ILogger<ResetDashboard> logger)
    {
        _activityLogger = activityLogger;
        _logger = logger;

        var connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is required.");
        _taskQueue = new QueueClient(connectionString, "agent-tasks");
        _poisonQueue = new QueueClient(connectionString, "agent-tasks-poison");
    }

    [Function("ResetDashboard")]
    public async Task<IActionResult> Execute(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reset")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Dashboard RESET triggered — clearing activity log and queues");

        try
        {
            // Clear activity log
            var entriesCleared = await _activityLogger.ClearAsync(cancellationToken);

            // Clear queues
            await _taskQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await _poisonQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var taskProps = await _taskQueue.GetPropertiesAsync(cancellationToken);
            var poisonProps = await _poisonQueue.GetPropertiesAsync(cancellationToken);
            var queueCleared = taskProps.Value.ApproximateMessagesCount;
            var poisonCleared = poisonProps.Value.ApproximateMessagesCount;

            await _taskQueue.ClearMessagesAsync(cancellationToken);
            await _poisonQueue.ClearMessagesAsync(cancellationToken);

            _logger.LogWarning(
                "Dashboard RESET complete — {Activities} activity entries, {Queue} queued, {Poison} poison cleared",
                entriesCleared, queueCleared, poisonCleared);

            return new OkObjectResult(new
            {
                status = "reset",
                activitiesCleared = entriesCleared,
                queueMessagesCleared = queueCleared,
                poisonMessagesCleared = poisonCleared
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard reset failed");
            return new ObjectResult(new
            {
                status = "error",
                message = ex.Message
            })
            { StatusCode = 500 };
        }
    }
}
