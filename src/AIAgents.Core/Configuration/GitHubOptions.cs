namespace AIAgents.Core.Configuration;

/// <summary>
/// Configuration options for GitHub repository integration.
/// Bound to the "GitHub" configuration section.
/// Only used when <c>Git:Provider</c> is set to <c>"GitHub"</c>.
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>
    /// GitHub repository owner (user or organization).
    /// Example: <c>toddpick</c>
    /// </summary>
    public required string Owner { get; init; }

    /// <summary>
    /// GitHub repository name (without the owner prefix).
    /// Example: <c>my-app</c>
    /// </summary>
    public required string Repo { get; init; }

    /// <summary>
    /// GitHub Personal Access Token (classic) with <c>repo</c> scope.
    /// Used for PR creation, merge, and workflow dispatch.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// Preferred base branch for AI story branches and Copilot delegation bootstrap.
    /// Defaults to <c>main</c> when not configured.
    /// </summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>
    /// Optional default GitHub user alias for per-story PAT routing.
    /// When unset, the primary <see cref="Token"/> is used as the default.
    /// </summary>
    public string? DefaultUserAlias { get; init; }

    /// <summary>
    /// Optional alias-to-token map for per-story PAT routing.
    /// Example env var key format: <c>GitHub__UserTokens__john</c>.
    /// </summary>
    public Dictionary<string, string> UserTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional display-name/label to alias map for friendly story values.
    /// Example env var key format: <c>GitHub__UserAliases__John Doe</c> = <c>john</c>.
    /// </summary>
    public Dictionary<string, string> UserAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The filename of the GitHub Actions workflow to trigger for Level 5 autonomy.
    /// Must accept <c>workflow_dispatch</c> trigger.
    /// Example: <c>deploy.yml</c>
    /// </summary>
    public string? DeployWorkflow { get; init; }
}
