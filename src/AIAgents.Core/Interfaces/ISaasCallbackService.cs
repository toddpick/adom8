namespace AIAgents.Core.Interfaces;

/// <summary>
/// Sends real-time agent run status callbacks to the adom8.dev SaaS dashboard.
/// When SaaS Mode is disabled this is a no-op so the rest of the engine is
/// completely unaware of whether a SaaS is connected or not.
/// </summary>
public interface ISaasCallbackService
{
    /// <summary>
    /// Reports the start of an agent run to the SaaS.
    /// Fires-and-forgets — never throws; logs warnings on error.
    /// </summary>
    Task ReportAgentStartedAsync(
        string correlationId,
        string agentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the successful completion of an agent run to the SaaS.
    /// Fires-and-forgets — never throws; logs warnings on error.
    /// </summary>
    Task ReportAgentCompletedAsync(
        string correlationId,
        string agentType,
        int inputTokens,
        int outputTokens,
        int durationMs,
        string? resultSummary = null,
        string? prUrl = null,
        int? prNumber = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the failure of an agent run to the SaaS.
    /// Fires-and-forgets — never throws; logs warnings on error.
    /// </summary>
    Task ReportAgentFailedAsync(
        string correlationId,
        string agentType,
        string errorMessage,
        CancellationToken cancellationToken = default);
}
