using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// Git operations using LibGit2Sharp.
/// Handles cloning, branching, committing, and pushing.
/// </summary>
public sealed class GitOperations : IGitOperations
{
    private readonly GitOptions _options;
    private readonly ILogger<GitOperations> _logger;
    private readonly UsernamePasswordCredentials _credentials;

    public GitOperations(
        IOptions<GitOptions> options,
        ILogger<GitOperations> logger)
    {
        _options = options.Value;
        _logger = logger;
        _credentials = new UsernamePasswordCredentials
        {
            Username = _options.Username,
            Password = _options.Token
        };
    }

    public Task<string> EnsureBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        var basePath = _options.LocalBasePath ?? Path.Combine(Path.GetTempPath(), "ado-agent-repos");
        var repoDir = Path.Combine(basePath, SanitizeDirectoryName(branchName));

        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            _logger.LogInformation("Cloning repository to {RepoDir}", repoDir);
            Directory.CreateDirectory(repoDir);

            var cloneOptions = new CloneOptions
            {
                FetchOptions = { CredentialsProvider = (_, _, _) => _credentials }
            };
            Repository.Clone(_options.RepositoryUrl, repoDir, cloneOptions);
        }
        else
        {
            _logger.LogDebug("Repository already exists at {RepoDir}", repoDir);
        }

        using (var repo = new Repository(repoDir))
        {
            // Always fetch latest before branch operations
            var remote = repo.Network.Remotes["origin"];
            var fetchOptions = new FetchOptions
            {
                CredentialsProvider = (_, _, _) => _credentials
            };
            Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification), fetchOptions, null);

            // Clean working directory before any branch switch to avoid conflicts
            repo.Reset(ResetMode.Hard);
            repo.RemoveUntrackedFiles();

            var branch = repo.Branches[branchName];
            var remoteBranch = repo.Branches[$"origin/{branchName}"];

            if (branch is null && remoteBranch is not null)
            {
                // Remote branch exists but no local tracking branch — create it tracking the remote
                _logger.LogInformation("Creating local branch '{BranchName}' tracking 'origin/{BranchName}'",
                    branchName, branchName);
                branch = repo.CreateBranch(branchName, remoteBranch.Tip);
                repo.Branches.Update(branch, b => b.TrackedBranch = remoteBranch.CanonicalName);
            }
            else if (branch is null)
            {
                // Brand new branch — create from default branch
                var defaultBranch = repo.Head;
                _logger.LogInformation("Creating branch '{BranchName}' from '{DefaultBranch}'",
                    branchName, defaultBranch.FriendlyName);
                branch = repo.CreateBranch(branchName, defaultBranch.Tip);
            }
            else if (remoteBranch is not null)
            {
                // Both local and remote exist — delete local and recreate from remote tip
                // This avoids issues with UpdateTarget not moving HEAD correctly
                _logger.LogDebug("Resetting '{BranchName}' to match 'origin/{BranchName}'",
                    branchName, branchName);
                
                // Switch to detached HEAD first so we can delete the local branch
                Commands.Checkout(repo, remoteBranch.Tip, new CheckoutOptions
                {
                    CheckoutModifiers = CheckoutModifiers.Force
                });
                repo.Branches.Remove(branch);
                branch = repo.CreateBranch(branchName, remoteBranch.Tip);
                repo.Branches.Update(branch, b => b.TrackedBranch = remoteBranch.CanonicalName);
            }

            Commands.Checkout(repo, branch, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force
            });
            _logger.LogInformation("Checked out branch '{BranchName}'", branchName);
        }

        return Task.FromResult(repoDir);
    }

    public Task CommitAndPushAsync(string repositoryPath, string message, CancellationToken cancellationToken = default)
    {
        using var repo = new Repository(repositoryPath);

        // Stage all changes
        Commands.Stage(repo, "*");

        // Check if there are any staged changes
        var status = repo.RetrieveStatus();
        if (!status.IsDirty)
        {
            _logger.LogInformation("No changes to commit");
            return Task.CompletedTask;
        }

        // Commit
        var author = new Signature(_options.Name, _options.Email, DateTimeOffset.UtcNow);
        var committer = author;
        repo.Commit(message, author, committer);

        _logger.LogInformation("Committed: {Message}", message);

        // Push
        var remote = repo.Network.Remotes["origin"];
        var pushOptions = new PushOptions
        {
            CredentialsProvider = (_, _, _) => _credentials
        };

        var currentBranch = repo.Head;

        // Force push — these are AI-agent-owned feature branches, force is safe
        // This avoids all "non-fastforwardable" and "remote contains commits" errors
        var pushRefSpec = $"+refs/heads/{currentBranch.FriendlyName}:refs/heads/{currentBranch.FriendlyName}";
        repo.Network.Push(remote, pushRefSpec, pushOptions);

        _logger.LogInformation("Pushed branch '{BranchName}' to origin", currentBranch.FriendlyName);

        return Task.CompletedTask;
    }

    public async Task WriteFileAsync(string repositoryPath, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(repositoryPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        _logger.LogDebug("Wrote file: {RelativePath}", relativePath);
    }

    public async Task<string?> ReadFileAsync(string repositoryPath, string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(repositoryPath, relativePath);

        if (!File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var files = Directory
            .EnumerateFiles(repositoryPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                     && !f.EndsWith(".git"))
            .Select(f => Path.GetRelativePath(repositoryPath, f))
            .OrderBy(f => f)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public Task<IReadOnlyList<string>> GetChangedFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var repo = new Repository(repositoryPath);

            var currentBranch = repo.Head;

            // Find the default branch (main or master) on origin
            var defaultBranch = repo.Branches["origin/main"] ?? repo.Branches["origin/master"];
            if (defaultBranch is null)
            {
                _logger.LogWarning("No origin/main or origin/master branch found for diff");
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var mergeBase = repo.ObjectDatabase.FindMergeBase(currentBranch.Tip, defaultBranch.Tip);
            if (mergeBase is null)
            {
                _logger.LogWarning("No merge base found between {Current} and {Default}",
                    currentBranch.FriendlyName, defaultBranch.FriendlyName);
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var changes = repo.Diff.Compare<TreeChanges>(mergeBase.Tree, currentBranch.Tip.Tree);
            var files = changes
                .Where(c => c.Status != ChangeKind.Deleted)
                .Select(c => c.Path.Replace('\\', '/'))
                .Where(p => !p.StartsWith(".ado/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .ToList();

            _logger.LogInformation("Git diff found {Count} changed files on branch {Branch}",
                files.Count, currentBranch.FriendlyName);

            return Task.FromResult<IReadOnlyList<string>>(files);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get changed files via git diff");
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private static string SanitizeDirectoryName(string name)
    {
        return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
