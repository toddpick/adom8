namespace AIAgents.Core.Configuration;

/// <summary>
/// Configuration options for the AI completion provider.
/// Bound to the "AI" configuration section.
/// </summary>
public sealed class AIOptions
{
    public const string SectionName = "AI";

    /// <summary>
    /// The AI provider to use (e.g., "AzureOpenAI", "OpenAI").
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// The model deployment name or identifier.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// The endpoint URL for the AI service.
    /// Required for Azure OpenAI; optional for OpenAI (uses default).
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Maximum number of tokens to generate per completion request.
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Temperature for sampling (0.0 = deterministic, 1.0 = creative).
    /// </summary>
    public double Temperature { get; init; } = 0.3;

    /// <summary>
    /// Optional per-agent model overrides. Keys are agent names
    /// ("Planning", "Coding", "Testing", "Review", "Documentation").
    /// Any property left null in the profile inherits from the defaults above.
    /// </summary>
    /// <example>
    /// Use an expensive model for Planning/Coding and a cheap one for Documentation:
    ///   "AI:AgentModels:Documentation:Model": "gemini-2.0-flash"
    ///   "AI:AgentModels:Documentation:Provider": "OpenAI"
    ///   "AI:AgentModels:Documentation:Endpoint": "https://openrouter.ai/api"
    /// </example>
    public Dictionary<string, AgentAIProfile>? AgentModels { get; init; }

    /// <summary>
    /// Optional model tier presets. Keys are tier names ("Standard", "Premium", "Economy").
    /// Values are dictionaries mapping agent names to their AI profiles for that tier.
    /// Users select a tier on the work item, and all agents in that tier use the
    /// configured model/provider/endpoint.
    /// </summary>
    /// <example>
    /// Use Opus for Planning+Coding in Premium tier:
    ///   "AI:ModelTiers:Premium:Planning:Model": "claude-opus-4-20250514"
    ///   "AI:ModelTiers:Premium:Coding:Model": "claude-opus-4-20250514"
    ///   "AI:ModelTiers:Economy:Testing:Model": "gpt-4o-mini"
    ///   "AI:ModelTiers:Economy:Documentation:Model": "gemini-2.0-flash"
    /// </example>
    public Dictionary<string, Dictionary<string, AgentAIProfile>>? ModelTiers { get; init; }
}
