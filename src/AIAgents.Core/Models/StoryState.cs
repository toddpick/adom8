using System.Text.Json.Serialization;

namespace AIAgents.Core.Models;

/// <summary>
/// The persisted state for a story being processed by the agent pipeline.
/// Serialized to .ado/stories/US-{id}/state.json.
/// </summary>
public sealed class StoryState
{
    [JsonPropertyName("workItemId")]
    public int WorkItemId { get; set; }

    [JsonPropertyName("currentState")]
    public required string CurrentState { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("currentStage")]
    public string? CurrentStage { get; set; }

    [JsonPropertyName("lastActivityUtc")]
    public DateTime? LastActivityUtc { get; set; }

    [JsonPropertyName("agents")]
    public Dictionary<string, AgentStatus> Agents { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public ArtifactPaths Artifacts { get; set; } = new();

    [JsonPropertyName("decisions")]
    public List<Decision> Decisions { get; set; } = [];

    [JsonPropertyName("questions")]
    public List<Question> Questions { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<string> Blockers { get; set; } = [];

    [JsonPropertyName("handoffRef")]
    public StoryHandoffReference? HandoffRef { get; set; }

    [JsonPropertyName("acceptanceTrace")]
    public List<AcceptanceTraceItem> AcceptanceTrace { get; set; } = [];

    [JsonPropertyName("tokenUsage")]
    public StoryTokenUsage TokenUsage { get; set; } = new();
}

public sealed class StoryHandoffReference
{
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("stage")]
    public required string Stage { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AcceptanceTraceItem
{
    [JsonPropertyName("acceptanceId")]
    public required string AcceptanceId { get; set; }

    [JsonPropertyName("acceptanceText")]
    public required string AcceptanceText { get; set; }

    [JsonPropertyName("mappedSubTasks")]
    public List<string> MappedSubTasks { get; set; } = [];

    [JsonPropertyName("plannedArtifacts")]
    public List<string> PlannedArtifacts { get; set; } = [];
}
