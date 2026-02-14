namespace AIAgents.Core.Configuration;

/// <summary>
/// Per-agent AI model overrides. Any property left null inherits from the
/// default <see cref="AIOptions"/> values, so you only specify what differs.
/// </summary>
/// <example>
/// Config in local.settings.json (flat-key style):
///   "AI__AgentModels__Documentation__Model": "gemini-2.0-flash"
///   "AI__AgentModels__Documentation__Provider": "OpenAI"
///   "AI__AgentModels__Documentation__Endpoint": "https://openrouter.ai/api"
///   "AI__AgentModels__Documentation__ApiKey": "sk-or-..."
/// </example>
public sealed class AgentAIProfile
{
    /// <summary>
    /// Override the AI provider (e.g., "OpenAI", "AzureOpenAI").
    /// Null = inherit from default AI config.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Override the model name/deployment (e.g., "gemini-2.0-flash", "gpt-4o-mini").
    /// Null = inherit from default AI config.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Override the API key for this agent's model.
    /// Null = inherit from default AI config.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Override the endpoint URL for this agent's model.
    /// Null = inherit from default AI config.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Override max tokens for this agent.
    /// Null = inherit from default AI config.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Override temperature for this agent.
    /// Null = inherit from default AI config.
    /// </summary>
    public double? Temperature { get; init; }
}
