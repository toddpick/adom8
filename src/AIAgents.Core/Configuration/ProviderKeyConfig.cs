namespace AIAgents.Core.Configuration;

/// <summary>
/// Holds API credentials for a specific AI provider.
/// Used by the <see cref="AIClientFactory"/> to automatically resolve the
/// correct API key and endpoint when a model override switches providers.
/// </summary>
/// <example>
/// Function App settings (flat-key style):
///   "AI__ProviderKeys__OpenAI__ApiKey": "sk-..."
///   "AI__ProviderKeys__OpenAI__Endpoint": "https://api.openai.com/v1"
///   "AI__ProviderKeys__Google__ApiKey": "AIza..."
///   "AI__ProviderKeys__Google__Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai"
/// </example>
public sealed class ProviderKeyConfig
{
    /// <summary>API key for this provider.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base endpoint URL for this provider.
    /// Null = use the provider's default endpoint.
    /// </summary>
    public string? Endpoint { get; init; }
}
