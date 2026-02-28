using AIAgents.Core.Configuration;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Services;

public sealed record GitHubTokenSelection(string Alias, string Token);

public interface IGitHubTokenResolver
{
    GitHubTokenSelection ResolveForStory(StoryWorkItem workItem);
    GitHubTokenSelection ResolveDefault();
}

public sealed class GitHubTokenResolver : IGitHubTokenResolver
{
    private const string DefaultAlias = "default";

    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubTokenResolver> _logger;

    public GitHubTokenResolver(IOptions<GitHubOptions> options, ILogger<GitHubTokenResolver> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public GitHubTokenSelection ResolveForStory(StoryWorkItem workItem)
    {
        var requested = (workItem.GitHubUserAccount ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return ResolveDefault();
        }

        var requestedAlias = ResolveAlias(requested);
        if (TryGetTokenByAlias(requestedAlias, out var token))
        {
            return new GitHubTokenSelection(requestedAlias, token);
        }

        _logger.LogWarning(
            "WI-{WorkItemId} requested unknown GitHub user alias '{Alias}'. Falling back to default token.",
            workItem.Id,
            requested);

        return ResolveDefault();
    }

    public GitHubTokenSelection ResolveDefault()
    {
        var configuredDefault = (_options.DefaultUserAlias ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configuredDefault))
        {
            var normalizedAlias = ResolveAlias(configuredDefault);
            if (TryGetTokenByAlias(normalizedAlias, out var token))
            {
                return new GitHubTokenSelection(normalizedAlias, token);
            }

            _logger.LogWarning(
                "GitHub default user alias '{Alias}' is configured but has no token mapping. Using primary GitHub:Token fallback.",
                configuredDefault);
        }

        return new GitHubTokenSelection(DefaultAlias, _options.Token);
    }

    private bool TryGetTokenByAlias(string alias, out string token)
    {
        token = string.Empty;
        if (_options.UserTokens is null || _options.UserTokens.Count == 0)
        {
            return false;
        }

        if (!_options.UserTokens.TryGetValue(alias, out var mappedToken) || string.IsNullOrWhiteSpace(mappedToken))
        {
            return false;
        }

        token = mappedToken;
        return true;
    }

    private string ResolveAlias(string value)
    {
        if (_options.UserAliases is not null &&
            _options.UserAliases.TryGetValue(value, out var mappedAlias) &&
            !string.IsNullOrWhiteSpace(mappedAlias))
        {
            return mappedAlias.Trim();
        }

        return value;
    }
}
