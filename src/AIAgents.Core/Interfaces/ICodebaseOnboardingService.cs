using AIAgents.Core.Models;

namespace AIAgents.Core.Interfaces;

/// <summary>
/// API-only codebase onboarding service that generates and publishes .agent context artifacts.
/// </summary>
public interface ICodebaseOnboardingService
{
    Task<CodebaseOnboardingExecutionResult> GenerateAndPublishAsync(
        bool incremental,
        bool includeGitHistory,
        CancellationToken cancellationToken = default);

    Task<CodebaseAnalysisMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default);
}
