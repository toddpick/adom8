using System.Text;
using System.Text.Json;
using AIAgents.Functions.Models;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;

namespace AIAgents.Functions.Services;

/// <summary>
/// Azure Storage Queue implementation of <see cref="IAgentTaskQueue"/>.
/// </summary>
public sealed class AgentTaskQueue : IAgentTaskQueue
{
    private readonly string _connectionString;

    public AgentTaskQueue(IConfiguration configuration)
    {
        _connectionString = configuration["AzureWebJobsStorage"]!;
    }

    public async Task EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var queueClient = new QueueClient(_connectionString, "agent-tasks");
        var messageJson = JsonSerializer.Serialize(task);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(messageJson));
        await queueClient.SendMessageAsync(base64, cancellationToken);
    }
}
