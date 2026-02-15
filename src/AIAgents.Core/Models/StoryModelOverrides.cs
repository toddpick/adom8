namespace AIAgents.Core.Models;

/// <summary>
/// Per-story AI model overrides read from ADO work item custom fields.
/// Passed to <see cref="Interfaces.IAIClientFactory"/> to resolve the
/// correct AI client for each agent on a per-story basis.
/// 
/// Resolution priority (most specific wins):
/// 1. Per-agent model field (e.g., <see cref="PlanningModel"/>)
/// 2. <see cref="ModelTier"/> → maps to pre-configured tier profiles in config
/// 3. Config-level per-agent defaults (AI:AgentModels:{agentName})
/// 4. Global AI defaults (AI:Model)
/// </summary>
public sealed record StoryModelOverrides
{
    /// <summary>
    /// Model tier preset: "Standard", "Premium", or "Economy".
    /// Maps to a full set of per-agent profiles defined in AI:ModelTiers config.
    /// </summary>
    public string? ModelTier { get; init; }

    /// <summary>Override model name for the Planning agent.</summary>
    public string? PlanningModel { get; init; }

    /// <summary>Override model name for the Coding agent.</summary>
    public string? CodingModel { get; init; }

    /// <summary>Override model name for the Testing agent.</summary>
    public string? TestingModel { get; init; }

    /// <summary>Override model name for the Review agent.</summary>
    public string? ReviewModel { get; init; }

    /// <summary>Override model name for the Documentation agent.</summary>
    public string? DocumentationModel { get; init; }

    /// <summary>
    /// Returns the per-agent model override for the given agent name, or null
    /// if no override is set for that agent.
    /// </summary>
    public string? GetModelForAgent(string agentName) => agentName switch
    {
        "Planning" => PlanningModel,
        "Coding" => CodingModel,
        "Testing" => TestingModel,
        "Review" => ReviewModel,
        "Documentation" => DocumentationModel,
        _ => null
    };
}
