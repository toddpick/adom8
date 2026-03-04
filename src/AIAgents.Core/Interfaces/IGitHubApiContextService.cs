namespace AIAgents.Core.Interfaces;

/// <summary>
/// Provides GitHub REST API-based repository context operations for agent services.
/// Replaces local git clone operations for read and write access to repository content.
/// </summary>
public interface IGitHubApiContextService
{
    /// <summary>
    /// Gets the full recursive file tree for a branch (paths only, no content).
    /// </summary>
    Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct = default);

    /// <summary>
    /// Fetches content of specific files by path. Returns null values for missing files.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetFileContentsAsync(
        string branch,
        IReadOnlyList<string> paths,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a human-readable summary of recent commits on the branch.
    /// </summary>
    Task<string> GetRecentCommitSummaryAsync(string branch, int count = 20, CancellationToken ct = default);

    /// <summary>
    /// Writes a single file to a branch via GitHub API (creates or updates).
    /// </summary>
    Task WriteFileAsync(string branch, string path, string content, string commitMessage, CancellationToken ct = default);

    /// <summary>
    /// Writes multiple files to a branch atomically in a single commit via the GitHub Trees API.
    /// </summary>
    Task WriteFilesAsync(string branch, IReadOnlyDictionary<string, string> files, string commitMessage, CancellationToken ct = default);
}
