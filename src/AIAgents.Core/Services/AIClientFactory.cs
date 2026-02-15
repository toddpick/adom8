using System.Collections.Concurrent;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// Resolves per-agent <see cref="IAIClient"/> instances by merging configuration
/// defaults with optional per-story overrides from ADO work item fields.
/// 
/// Resolution priority (most specific wins):
/// 1. Story per-agent model field (e.g., Custom.AIPlanningModel on the work item)
/// 2. Story model tier → maps to AI:ModelTiers:{tier}:{agentName} config
/// 3. Config per-agent defaults → AI:AgentModels:{agentName}
/// 4. Global defaults → AI:Provider / AI:Model / etc.
/// 
/// Instances are cached by effective configuration fingerprint so agents
/// that resolve to the same model/provider reuse the same client.
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
    public IAIClient GetClientForAgent(string agentName, StoryModelOverrides? storyOverrides = null)
    {
        var effective = ResolveEffectiveOptions(agentName, storyOverrides);
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
    /// Merges the default AI options with config-level per-agent overrides,
    /// then applies story-level overrides (tier and per-agent fields).
    /// </summary>
    internal AIOptions ResolveEffectiveOptions(string agentName, StoryModelOverrides? storyOverrides = null)
    {
        // Step 1: Start with global defaults
        var provider = _defaults.Provider;
        var model = _defaults.Model;
        var apiKey = _defaults.ApiKey;
        var endpoint = _defaults.Endpoint;
        var maxTokens = _defaults.MaxTokens;
        var temperature = _defaults.Temperature;

        // Step 2: Apply config-level per-agent overrides (AI:AgentModels:{agentName})
        if (_defaults.AgentModels?.TryGetValue(agentName, out var configProfile) == true)
        {
            provider = configProfile.Provider ?? provider;
            model = configProfile.Model ?? model;
            apiKey = configProfile.ApiKey ?? apiKey;
            endpoint = configProfile.Endpoint ?? endpoint;
            maxTokens = configProfile.MaxTokens ?? maxTokens;
            temperature = configProfile.Temperature ?? temperature;
        }

        if (storyOverrides is not null)
        {
            // Step 3: Apply story-level tier overrides (AI:ModelTiers:{tier}:{agentName})
            if (storyOverrides.ModelTier is not null &&
                _defaults.ModelTiers?.TryGetValue(storyOverrides.ModelTier, out var tierAgents) == true &&
                tierAgents.TryGetValue(agentName, out var tierProfile))
            {
                provider = tierProfile.Provider ?? provider;
                model = tierProfile.Model ?? model;
                apiKey = tierProfile.ApiKey ?? apiKey;
                endpoint = tierProfile.Endpoint ?? endpoint;
                maxTokens = tierProfile.MaxTokens ?? maxTokens;
                temperature = tierProfile.Temperature ?? temperature;
            }

            // Step 4: Apply story-level per-agent model override (highest priority)
            var storyModel = storyOverrides.GetModelForAgent(agentName);
            if (!string.IsNullOrWhiteSpace(storyModel))
            {
                model = storyModel;
                _logger.LogInformation(
                    "Story-level model override for agent '{Agent}': {Model}",
                    agentName, storyModel);
            }
        }

        return new AIOptions
        {
            Provider = provider,
            Model = model,
            ApiKey = apiKey,
            Endpoint = endpoint,
            MaxTokens = maxTokens,
            Temperature = temperature
        };
    }

    /// <summary>
    /// Builds a cache key from the effective options so that agents with
    /// identical resolved configurations share one <see cref="AIClient"/>.
    /// </summary>
    private static string BuildCacheKey(AIOptions options)
        => $"{options.Provider}|{options.Model}|{options.Endpoint}|{options.ApiKey?.GetHashCode()}";
}
