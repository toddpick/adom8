using System.Text;
using System.Text.Json;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Telemetry;
using AIAgents.Functions.Models;
using Azure.Storage.Queues;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Timer-triggered function that processes the dead letter (poison) queue.
/// Runs every 15 minutes. For each failed message:
/// - Parses the original AgentTask
/// - Posts a detailed failure comment on the Azure DevOps work item
/// - Updates the work item state to "Agent Failed"
/// - Logs telemetry for alerting
/// - Deletes the message from the poison queue
/// </summary>
public sealed class DeadLetterQueueHandler
{
    private readonly IAzureDevOpsClient _adoClient;
    private readonly ILogger<DeadLetterQueueHandler> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly QueueClient _poisonQueueClient;

    public DeadLetterQueueHandler(
        IAzureDevOpsClient adoClient,
        ILogger<DeadLetterQueueHandler> logger,
        TelemetryClient telemetry,
        IConfiguration configuration)
    {
        _adoClient = adoClient;
        _logger = logger;
        _telemetry = telemetry;

        var connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is required.");
        _poisonQueueClient = new QueueClient(connectionString, "agent-tasks-poison");
    }

    [Function("DeadLetterQueueHandler")]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Dead letter queue handler started");

        await _poisonQueueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var properties = await _poisonQueueClient.GetPropertiesAsync(cancellationToken);
        var messageCount = properties.Value.ApproximateMessagesCount;

        if (messageCount == 0)
        {
            _logger.LogInformation("No messages in poison queue");
            return;
        }

        _logger.LogWarning("Found {MessageCount} messages in poison queue", messageCount);

        // Process up to 32 messages per run
        var messages = await _poisonQueueClient.ReceiveMessagesAsync(
            maxMessages: 32,
            visibilityTimeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        var processed = 0;

        foreach (var message in messages.Value)
        {
            try
            {
                await ProcessPoisonMessageAsync(message.MessageText, cancellationToken);
                await _poisonQueueClient.DeleteMessageAsync(
                    message.MessageId, message.PopReceipt, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process poison message {MessageId}", message.MessageId);
            }
        }

        _logger.LogInformation(
            "Dead letter queue handler completed: {Processed}/{Total} messages processed",
            processed, messages.Value.Length);
    }

    private async Task ProcessPoisonMessageAsync(string messageText, CancellationToken ct)
    {
        // Messages may be Base64-encoded
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(messageText));
        }
        catch (FormatException)
        {
            decoded = messageText; // Not Base64, use as-is
        }

        AgentTask? task;
        try
        {
            task = JsonSerializer.Deserialize<AgentTask>(decoded);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Cannot parse poison message as AgentTask: {Message}", decoded);
            return;
        }

        if (task is null)
        {
            _logger.LogError("Deserialized poison message is null: {Message}", decoded);
            return;
        }

        _logger.LogWarning(
            "Processing dead letter: {AgentType} agent for WI-{WorkItemId} (correlation: {CorrelationId})",
            task.AgentType, task.WorkItemId, task.CorrelationId);

        // Telemetry
        _telemetry.TrackEvent(TelemetryEvents.DeadLetterProcessed, new Dictionary<string, string>
        {
            [TelemetryProperties.WorkItemId] = task.WorkItemId.ToString(),
            [TelemetryProperties.AgentType] = task.AgentType.ToString(),
            [TelemetryProperties.CorrelationId] = task.CorrelationId
        });

        // Post comment to work item
        if (task.WorkItemId > 0)
        {
            var comment = FormatDeadLetterComment(task);

            try
            {
                await _adoClient.AddWorkItemCommentAsync(task.WorkItemId, comment, ct);
                await _adoClient.UpdateWorkItemFieldAsync(task.WorkItemId, CustomFieldNames.Paths.CurrentAIAgent, ToCurrentAgentValue(task.AgentType), ct);
                await _adoClient.UpdateWorkItemStateAsync(task.WorkItemId, "Agent Failed", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to update work item WI-{WorkItemId} from dead letter handler",
                    task.WorkItemId);
            }
        }
    }

    internal static string FormatDeadLetterComment(AgentTask task)
    {
        return $"""
            ❌ Agent execution failed after maximum retry attempts

            **Failed Agent:** {task.AgentType}
            **Error Category:** Exhausted all retries (transient or code error)
            **CorrelationId:** {task.CorrelationId}
            **Originally Enqueued:** {task.EnqueuedAt:u}

            **Troubleshooting Steps:**
            1. Check Application Insights logs with CorrelationId above
            2. Verify configuration: AI API key, Git credentials, ADO PAT
            3. Review work item description for issues
            4. See TROUBLESHOOTING.md for common solutions

            **To retry:** Update work item state back to the appropriate trigger state to re-process
            """;
    }

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
