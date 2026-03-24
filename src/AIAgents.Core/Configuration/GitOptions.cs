namespace AIAgents.Core.Configuration;

/// <summary>
/// Configuration options for Git operations via LibGit2Sharp.
/// Bound to the "Git" configuration section.
/// </summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>
    /// The repository hosting provider: <c>"GitHub"</c> or <c>"AzureDevOps"</c>.
    /// Determines which <see cref="Interfaces.IRepositoryProvider"/> implementation is used for PRs.
    /// </summary>
    public string Provider { get; init; } = "GitHub";

    /// <summary>
    /// The remote repository URL to clone/push to.
    /// For GitHub: <c>https://github.com/owner/repo.git</c>
    /// For Azure DevOps: <c>https://dev.azure.com/org/project/_git/repo</c>
    /// </summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>
    /// Username for Git authentication (typically the PAT user or "x-token-auth").
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Token or password for Git authentication.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// Email address used for Git commit author information.
    /// </summary>
    public required string Email { get; init; }

    /// <summary>
    /// Display name used for Git commit author information.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Preferred base branch for creating new AI feature branches.
    /// Leave blank to use the repository provider's default branch behavior.
    /// </summary>
    public string BaseBranch { get; init; } = string.Empty;

    /// <summary>
    /// Local directory path for cloning repositories.
    /// Defaults to a temp directory if not specified.
    /// </summary>
    public string? LocalBasePath { get; init; }
}
