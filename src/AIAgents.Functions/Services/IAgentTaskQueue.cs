using AIAgents.Functions.Models;

namespace AIAgents.Functions.Services;

/// <summary>
/// Abstraction for enqueuing the next agent task onto the queue.
/// Allows unit tests to mock queue operations without requiring Azurite.
/// </summary>
public interface IAgentTaskQueue
{
    /// <summary>
    /// Enqueues an agent task for processing by the next agent in the pipeline.
    /// </summary>
    Task EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default);
}
