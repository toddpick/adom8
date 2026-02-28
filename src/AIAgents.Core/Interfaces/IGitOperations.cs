namespace AIAgents.Core.Interfaces;

/// <summary>
/// Abstracts Git operations via LibGit2Sharp.
/// </summary>
public interface IGitOperations
{
    /// <summary>
    /// Clones or opens the repository and checks out the specified branch.
    /// Creates the branch from the default branch if it does not exist.
    /// </summary>
    /// <param name="branchName">The branch to check out or create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The local repository path.</returns>
    Task<string> EnsureBranchAsync(string branchName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones or opens the repository and prepares the specified branch.
    /// When <paramref name="lightweightCheckout"/> is true, avoids full working-tree checkout.
    /// </summary>
    Task<string> EnsureBranchAsync(string branchName, bool lightweightCheckout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hydrates only the specified paths into the working tree for lightweight clones.
    /// Paths must be repository-relative.
    /// </summary>
    Task HydrateWorkingTreeAsync(string repositoryPath, IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages all changes, commits with the given message, and pushes to origin.
    /// </summary>
    /// <param name="repositoryPath">Local path to the repository.</param>
    /// <param name="message">Commit message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CommitAndPushAsync(string repositoryPath, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes content to a file in the repository working directory.
    /// Creates parent directories as needed.
    /// </summary>
    /// <param name="repositoryPath">Local path to the repository root.</param>
    /// <param name="relativePath">Relative path within the repository.</param>
    /// <param name="content">File content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteFileAsync(string repositoryPath, string relativePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the content of a file from the repository working directory.
    /// </summary>
    /// <param name="repositoryPath">Local path to the repository root.</param>
    /// <param name="relativePath">Relative path within the repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file content, or null if the file does not exist.</returns>
    Task<string?> ReadFileAsync(string repositoryPath, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all files in the repository (relative paths).
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns relative paths of files changed between the current branch and the default branch (main/master).
    /// Excludes deleted files and .ado/ story artifacts.
    /// Used as a fallback when artifact tracking is empty.
    /// </summary>
    Task<IReadOnlyList<string>> GetChangedFilesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the local repository clone at the given path to free temp disk space.
    /// Safe to call even if the directory does not exist.
    /// </summary>
    Task CleanupRepoAsync(string repositoryPath, CancellationToken cancellationToken = default);
}
