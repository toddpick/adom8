namespace AIAgents.Core.Models;

/// <summary>
/// Output summary from API-only codebase onboarding.
/// </summary>
public sealed class CodebaseOnboardingExecutionResult
{
    public required string Branch { get; init; }
    public required string HeadSha { get; init; }
    public required int FilesScanned { get; init; }
    public required long TotalBytesScanned { get; init; }
    public required int ArtifactsPublished { get; init; }
    public required int ActiveAuthors { get; init; }
    public required DateTime? LastCommitDateUtc { get; init; }
    public required string Summary { get; init; }
    public required CodebaseAnalysisMetadata Metadata { get; init; }
}
