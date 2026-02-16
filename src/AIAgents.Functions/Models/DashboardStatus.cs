using System.Text.Json.Serialization;

namespace AIAgents.Functions.Models;

/// <summary>
/// Response model for the GET /api/status endpoint consumed by the dashboard.
/// </summary>
public sealed class DashboardStatus
{
    [JsonPropertyName("currentWorkItem")]
    public CurrentWorkItemInfo? CurrentWorkItem { get; init; }

    [JsonPropertyName("stories")]
    public required IReadOnlyList<StoryStatus> Stories { get; init; }

    [JsonPropertyName("stats")]
    public required DashboardStats Stats { get; init; }

    [JsonPropertyName("recentActivity")]
    public required IReadOnlyList<ActivityEntry> RecentActivity { get; init; }

    [JsonPropertyName("queuedTasks")]
    public IReadOnlyList<QueuedTaskInfo> QueuedTasks { get; init; } = [];

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Lightweight representation of a task waiting in the queue.
/// </summary>
public sealed class QueuedTaskInfo
{
    [JsonPropertyName("workItemId")]
    public required int WorkItemId { get; init; }

    [JsonPropertyName("agentType")]
    public required string AgentType { get; init; }

    [JsonPropertyName("enqueuedAt")]
    public DateTime EnqueuedAt { get; init; }
}

/// <summary>
/// Represents the current work item being processed by the agent pipeline.
/// </summary>
public sealed class CurrentWorkItemInfo
{
    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("autonomyLevel")]
    public string? AutonomyLevel { get; init; }

    [JsonPropertyName("elapsedTime")]
    public string? ElapsedTime { get; init; }
}

/// <summary>
/// Status of a single story in the agent pipeline.
/// </summary>
public sealed class StoryStatus
{
    [JsonPropertyName("workItemId")]
    public required int WorkItemId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("currentAgent")]
    public string? CurrentAgent { get; init; }

    [JsonPropertyName("progress")]
    public int Progress { get; init; }

    [JsonPropertyName("agents")]
    public required IReadOnlyDictionary<string, string> Agents { get; init; }

    [JsonPropertyName("tokenUsage")]
    public StoryTokenUsageDto? TokenUsage { get; init; }

    [JsonPropertyName("agentTimings")]
    public IReadOnlyDictionary<string, AgentTimingDto>? AgentTimings { get; init; }
}

/// <summary>
/// Timing information for a single agent's execution.
/// </summary>
public sealed class AgentTimingDto
{
    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; init; }
}

/// <summary>
/// Aggregate statistics for the dashboard header cards.
/// </summary>
public sealed class DashboardStats
{
    [JsonPropertyName("storiesProcessed")]
    public int StoriesProcessed { get; init; }

    [JsonPropertyName("agentsActive")]
    public int AgentsActive { get; init; }

    [JsonPropertyName("successRate")]
    public double SuccessRate { get; init; }

    [JsonPropertyName("avgProcessingTime")]
    public string AvgProcessingTime { get; init; } = "N/A";

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; init; }

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; init; }
}

/// <summary>
/// A single activity log entry for the dashboard feed.
/// </summary>
public sealed class ActivityEntry
{
    [JsonPropertyName("timestamp")]
    public required DateTime Timestamp { get; init; }

    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("workItemId")]
    public required int WorkItemId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("level")]
    public string Level { get; init; } = "info";

    [JsonPropertyName("tokens")]
    public int Tokens { get; init; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; init; }
}

/// <summary>
/// Lightweight token usage DTO for the dashboard, without the full agent breakdown.
/// </summary>
public sealed class StoryTokenUsageDto
{
    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; init; }

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; init; }

    [JsonPropertyName("complexity")]
    public string Complexity { get; init; } = "XS";

    [JsonPropertyName("agents")]
    public IReadOnlyDictionary<string, AgentTokenUsageDto>? Agents { get; init; }
}

/// <summary>
/// Per-agent token usage for the dashboard.
/// </summary>
public sealed class AgentTokenUsageDto
{
    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; init; }

    [JsonPropertyName("estimatedCost")]
    public decimal EstimatedCost { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("callCount")]
    public int CallCount { get; init; }
}
