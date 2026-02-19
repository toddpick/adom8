using System.Text.Json.Serialization;

namespace AIAgents.Core.Models;

/// <summary>
/// All context needed by a coding strategy to implement a story.
/// Passed from <see cref="AIAgents.Functions.Agents.CodingAgentService"/> to the
/// active <see cref="AIAgents.Core.Interfaces.ICodingStrategy"/>.
/// </summary>
public sealed record CodingContext
{
    /// <summary>Azure DevOps work item ID.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>Local path to the cloned repository on the feature branch.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Current pipeline state loaded from state.json.</summary>
    public required StoryState State { get; init; }

    /// <summary>The work item data from ADO.</summary>
    public required StoryWorkItem WorkItem { get; init; }

    /// <summary>Implementation plan markdown from the Planning agent.</summary>
    public required string PlanMarkdown { get; init; }

    /// <summary>Coding guidelines and codebase context from .agent/ docs.</summary>
    public string CodingGuidelines { get; init; } = "";

    /// <summary>Summary of existing files in the repository.</summary>
    public string ExistingFilesSummary { get; init; } = "";

    /// <summary>
    /// Repository-relative folder containing supporting files materialized from the ADO work item.
    /// </summary>
    public string StoryDocumentsFolder { get; init; } = string.Empty;

    /// <summary>
    /// Repository-relative paths to image attachments materialized from the ADO work item.
    /// </summary>
    public IReadOnlyList<string> AttachedImagePaths { get; init; } = [];

    /// <summary>
    /// Repository-relative paths to document attachments materialized from the ADO work item.
    /// </summary>
    public IReadOnlyList<string> AttachedDocumentPaths { get; init; } = [];

    /// <summary>
    /// The feature branch name (e.g., "feature/US-123").
    /// Used by Copilot strategy to instruct Copilot which branch to target.
    /// </summary>
    public required string BranchName { get; init; }

    /// <summary>Correlation ID for end-to-end tracing.</summary>
    public string CorrelationId { get; init; } = "";
}

/// <summary>
/// Result from a coding strategy execution.
/// </summary>
public sealed record CodingResult
{
    /// <summary>Whether the coding completed successfully.</summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Execution mode identifier.
    /// "agentic" = built-in tool-use loop, "copilot-delegated" = delegated to GitHub Copilot.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>Files modified during coding (relative paths). Empty for copilot-delegated until bridge reconciles.</summary>
    public List<string> ModifiedFiles { get; init; } = [];

    /// <summary>Total tokens consumed (0 for Copilot — subscription-based).</summary>
    public int Tokens { get; init; }

    /// <summary>Estimated cost in USD (0 for Copilot — subscription-based).</summary>
    public decimal Cost { get; init; }

    /// <summary>Human-readable summary of what was done.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Error message if <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Copilot-specific metrics. Null for agentic mode.</summary>
    public CopilotMetrics? CopilotMetrics { get; init; }

    /// <summary>Agentic-specific metadata. Null for copilot mode.</summary>
    public AgenticMetrics? AgenticMetrics { get; init; }
}

/// <summary>
/// Work metrics from a Copilot coding agent delegation.
/// Copilot doesn't expose token usage, so we track meaningful "effort" metrics instead.
/// </summary>
public sealed record CopilotMetrics
{
    /// <summary>GitHub Issue number used to trigger Copilot (0 if CreateIssue=false).</summary>
    [JsonPropertyName("issueNumber")]
    public int IssueNumber { get; init; }

    /// <summary>Copilot's PR number.</summary>
    [JsonPropertyName("pullRequestNumber")]
    public int PullRequestNumber { get; init; }

    /// <summary>Number of files changed.</summary>
    [JsonPropertyName("filesChanged")]
    public int FilesChanged { get; init; }

    /// <summary>Lines of code added.</summary>
    [JsonPropertyName("linesAdded")]
    public int LinesAdded { get; init; }

    /// <summary>Lines of code deleted.</summary>
    [JsonPropertyName("linesDeleted")]
    public int LinesDeleted { get; init; }

    /// <summary>Time in minutes from delegation to PR creation.</summary>
    [JsonPropertyName("durationMinutes")]
    public double DurationMinutes { get; init; }

    /// <summary>Number of commits Copilot made.</summary>
    [JsonPropertyName("commitCount")]
    public int CommitCount { get; init; }
}

/// <summary>
/// Metadata from the built-in agentic coding loop.
/// </summary>
public sealed record AgenticMetrics
{
    /// <summary>Number of agentic loop rounds executed.</summary>
    [JsonPropertyName("rounds")]
    public int Rounds { get; init; }

    /// <summary>Total tool calls made by the AI.</summary>
    [JsonPropertyName("toolCalls")]
    public int ToolCalls { get; init; }

    /// <summary>Whether the loop completed naturally (vs hitting MaxRounds).</summary>
    [JsonPropertyName("completedNaturally")]
    public bool CompletedNaturally { get; init; }
}
