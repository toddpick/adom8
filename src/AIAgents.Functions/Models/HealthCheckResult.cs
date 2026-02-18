using System.Text.Json.Serialization;

namespace AIAgents.Functions.Models;

/// <summary>
/// Health check response model returned by the /api/health endpoint.
/// Reports status of all system components with response times and details.
/// </summary>
public sealed class HealthCheckResult
{
    /// <summary>Overall system status: healthy, degraded, or unhealthy.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>When the health check was performed (UTC).</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Individual component check results keyed by component name.</summary>
    [JsonPropertyName("checks")]
    public Dictionary<string, ComponentCheck> Checks { get; init; } = new();

    /// <summary>Application version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    /// <summary>Deployment environment (dev, staging, prod).</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>AI provider configuration and status for dashboard display.</summary>
    [JsonPropertyName("providers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderInfo? Providers { get; init; }
}

/// <summary>
/// AI provider configuration info returned alongside health checks.
/// Used by the dashboard to show which providers are configured and their models.
/// </summary>
public sealed class ProviderInfo
{
    /// <summary>Primary AI provider (Claude, OpenAI, etc.).</summary>
    [JsonPropertyName("ai")]
    public ProviderDetail? Ai { get; init; }

    /// <summary>GitHub Copilot coding agent configuration.</summary>
    [JsonPropertyName("copilot")]
    public CopilotProviderDetail? Copilot { get; init; }

    /// <summary>Additional configured providers from ProviderKeys.</summary>
    [JsonPropertyName("additionalProviders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProviderDetail>? AdditionalProviders { get; init; }
}

/// <summary>
/// Details for a single AI provider.
/// </summary>
public sealed class ProviderDetail
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>
/// Details for the GitHub Copilot provider.
/// </summary>
public sealed class CopilotProviderDetail
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }
}

/// <summary>
/// Status of an individual health check component (ADO, queue, AI API, etc.).
/// </summary>
public sealed class ComponentCheck
{
    /// <summary>Component status: healthy, degraded, or unhealthy.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Response time in milliseconds, if applicable.</summary>
    [JsonPropertyName("responseTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ResponseTime { get; init; }

    /// <summary>Queue message count, if applicable.</summary>
    [JsonPropertyName("messageCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageCount { get; init; }

    /// <summary>Poison queue message count, if applicable.</summary>
    [JsonPropertyName("poisonMessageCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PoisonMessageCount { get; init; }

    /// <summary>Additional detail or error message.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>Missing environment variables, if any.</summary>
    [JsonPropertyName("missingVars")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MissingVars { get; init; }
}
