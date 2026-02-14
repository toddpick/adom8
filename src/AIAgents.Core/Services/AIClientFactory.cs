using System.Collections.Concurrent;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// Resolves per-agent <see cref="IAIClient"/> instances by merging the default
/// <see cref="AIOptions"/> with optional <see cref="AgentAIProfile"/> overrides.
/// Instances are cached by effective configuration so agents that share the
/// same model/provider reuse the same client.
/// </summary>
public sealed class AIClientFactory : IAIClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AIOptions _defaults;
    private readonly ILogger<AIClient> _logger;

    // Cache keyed by a fingerprint of the effective options so identical
    // configs share one AIClient instance.
    private readonly ConcurrentDictionary<string, IAIClient> _cache = new();

    public AIClientFactory(
        IHttpClientFactory httpClientFactory,
        IOptions<AIOptions> defaults,
        ILogger<AIClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _defaults = defaults.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IAIClient GetClientForAgent(string agentName)
    {
        var effective = ResolveEffectiveOptions(agentName);
        var cacheKey = BuildCacheKey(effective);

        return _cache.GetOrAdd(cacheKey, _ =>
        {
            _logger.LogInformation(
                "Creating AI client for agent '{Agent}' → provider={Provider}, model={Model}, endpoint={Endpoint}",
                agentName, effective.Provider, effective.Model, effective.Endpoint ?? "(default)");

            return new AIClient(_httpClientFactory, effective, _logger);
        });
    }

    /// <summary>
    /// Merges the default AI options with any per-agent overrides.
    /// Null override properties fall through to the defaults.
    /// </summary>
    private AIOptions ResolveEffectiveOptions(string agentName)
    {
        if (_defaults.AgentModels is null ||
            !_defaults.AgentModels.TryGetValue(agentName, out var profile))
        {
            return _defaults;
        }

        return new AIOptions
        {
            Provider = profile.Provider ?? _defaults.Provider,
            Model = profile.Model ?? _defaults.Model,
            ApiKey = profile.ApiKey ?? _defaults.ApiKey,
            Endpoint = profile.Endpoint ?? _defaults.Endpoint,
            MaxTokens = profile.MaxTokens ?? _defaults.MaxTokens,
            Temperature = profile.Temperature ?? _defaults.Temperature
        };
    }

    /// <summary>
    /// Builds a cache key from the effective options so that agents with
    /// identical resolved configurations share one <see cref="AIClient"/>.
    /// </summary>
    private static string BuildCacheKey(AIOptions options)
        => $"{options.Provider}|{options.Model}|{options.Endpoint}|{options.ApiKey?.GetHashCode()}";
}
