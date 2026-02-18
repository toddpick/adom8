using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Emergency stop endpoint — clears all agent task queues to halt AI processing.
/// This is the "abort" button for when agents are misbehaving or burning tokens.
/// POST /api/emergency-stop — clears agent-tasks and agent-tasks-poison queues.
/// GET  /api/emergency-stop — returns current queue depths (safe to poll).
/// </summary>
public sealed class EmergencyStop
{
    private readonly QueueClient _taskQueue;
    private readonly QueueClient _poisonQueue;
    private readonly ILogger<EmergencyStop> _logger;

    public EmergencyStop(
        IConfiguration configuration,
        ILogger<EmergencyStop> logger)
    {
        _logger = logger;

        var connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is required.");
        _taskQueue = new QueueClient(connectionString, "agent-tasks");
        _poisonQueue = new QueueClient(connectionString, "agent-tasks-poison");
    }

    /// <summary>
    /// GET /api/emergency-stop — returns current queue depths without modifying anything.
    /// Dashboard polls this to show live queue status on the abort button.
    /// </summary>
    [Function("EmergencyStopStatus")]
    public async Task<IActionResult> GetStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "emergency-stop")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        try
        {
            await _taskQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await _poisonQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var taskProps = await _taskQueue.GetPropertiesAsync(cancellationToken);
            var poisonProps = await _poisonQueue.GetPropertiesAsync(cancellationToken);

            return new OkObjectResult(new
            {
                queueDepth = taskProps.Value.ApproximateMessagesCount,
                poisonQueueDepth = poisonProps.Value.ApproximateMessagesCount,
                status = "monitoring"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check queue status");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>
    /// POST /api/emergency-stop — clears all queues, stopping all pending agent work.
    /// This is the nuclear option — any in-flight messages will complete but nothing new will start.
    /// </summary>
    [Function("EmergencyStopExecute")]
    public async Task<IActionResult> Execute(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "emergency-stop")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("EMERGENCY STOP triggered — clearing all agent task queues");

        var cleared = 0;
        var poisonCleared = 0;

        try
        {
            await _taskQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await _poisonQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            // Get counts before clearing
            var taskProps = await _taskQueue.GetPropertiesAsync(cancellationToken);
            var poisonProps = await _poisonQueue.GetPropertiesAsync(cancellationToken);
            cleared = taskProps.Value.ApproximateMessagesCount;
            poisonCleared = poisonProps.Value.ApproximateMessagesCount;

            // Clear both queues
            await _taskQueue.ClearMessagesAsync(cancellationToken);
            await _poisonQueue.ClearMessagesAsync(cancellationToken);

            _logger.LogWarning(
                "EMERGENCY STOP complete — cleared {TaskCount} task messages and {PoisonCount} poison messages",
                cleared, poisonCleared);

            return new OkObjectResult(new
            {
                status = "stopped",
                messagesCleared = cleared,
                poisonMessagesCleared = poisonCleared,
                message = $"Emergency stop executed. Cleared {cleared} pending tasks and {poisonCleared} poison messages. Any currently running agent will finish but no new agents will start.",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency stop failed");
            return new ObjectResult(new
            {
                status = "error",
                message = $"Emergency stop failed: {ex.Message}"
            })
            { StatusCode = 500 };
        }
    }
}
