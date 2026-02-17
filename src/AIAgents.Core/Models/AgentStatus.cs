using System.Text.Json.Serialization;

namespace AIAgents.Core.Models;

/// <summary>
/// Tracks the execution status of a single agent for a story.
/// </summary>
public sealed class AgentStatus
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("additionalData")]
    public Dictionary<string, object>? AdditionalData { get; set; }

    public static AgentStatus Pending() => new() { Status = "pending" };
    public static AgentStatus InProgress() => new() { Status = "in_progress", StartedAt = DateTime.UtcNow };
    public static AgentStatus Completed() => new() { Status = "completed", CompletedAt = DateTime.UtcNow };
    public static AgentStatus Failed(string? reason = null) => new()
    {
        Status = "failed",
        CompletedAt = DateTime.UtcNow,
        AdditionalData = reason is not null ? new() { ["error"] = reason } : null
    };
    public static AgentStatus Skipped(string? reason = null) => new()
    {
        Status = "skipped",
        CompletedAt = DateTime.UtcNow,
        AdditionalData = reason is not null ? new() { ["reason"] = reason } : null
    };
}
