using System.Diagnostics;
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
        => EnsureBranchAsync(branchName, lightweightCheckout: false, cancellationToken);

    public async Task<string> EnsureBranchAsync(string branchName, bool lightweightCheckout, CancellationToken cancellationToken = default)
    {
        var basePath = _options.LocalBasePath ?? GetDefaultBasePath();
        var repoDir = Path.Combine(basePath, SanitizeDirectoryName(branchName));

        // Sweep stale clone dirs to prevent disk exhaustion on Consumption plans.
        // Phase 1: remove anything older than 30 minutes (agent runs complete well within this window).
        // Phase 2: if free disk space is critically low (< 300 MB), remove ALL sibling dirs regardless
        //          of age so that the upcoming clone has room to proceed.
        if (Directory.Exists(basePath))
        {
            var threshold = DateTime.UtcNow.AddMinutes(-30);
            foreach (var staleDir in Directory.EnumerateDirectories(basePath))
            {
                if (string.Equals(staleDir, repoDir, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.GetLastWriteTimeUtc(staleDir) < threshold)
                    {
                        _logger.LogInformation("Sweeping stale repo dir: {Dir}", staleDir);
                        DeleteDirectory(staleDir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not sweep stale repo dir {Dir}", staleDir);
                }
            }

            // Emergency sweep: if fewer than 300 MB free on the temp drive, nuke all sibling
            // clone dirs (even recent ones) so the clone below has a fighting chance.
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(basePath) ?? basePath);
                const long freeMbThreshold = 300L * 1024 * 1024;
                if (drive.AvailableFreeSpace < freeMbThreshold)
                {
                    _logger.LogWarning(
                        "Low disk space ({FreeMb} MB free) — sweeping ALL repo dirs to reclaim space",
                        drive.AvailableFreeSpace / (1024 * 1024));
                    foreach (var dir in Directory.EnumerateDirectories(basePath))
                    {
                        if (string.Equals(dir, repoDir, StringComparison.OrdinalIgnoreCase)) continue;
                        try { DeleteDirectory(dir); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Could not sweep repo dir {Dir}", dir); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check available disk space on {BasePath}", basePath);
            }
        }

        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            // If repoDir exists but has no .git, a previous clone attempt failed partway
            // and left partial debris on disk. Delete it first so we start clean and free
            // that space before the new clone writes its data.
            if (Directory.Exists(repoDir))
            {
                _logger.LogWarning("Partial/failed clone detected at {RepoDir} — deleting before retry", repoDir);
                try { DeleteDirectory(repoDir); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial clone dir {RepoDir}", repoDir); }
            }

            _logger.LogInformation("Shallow-cloning repository (depth=1) to {RepoDir}", repoDir);
            Directory.CreateDirectory(repoDir);
            await ShallowCloneAsync(_options.RepositoryUrl, repoDir, _options.Username, _options.Token, _logger, lightweightCheckout);
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

            // For lightweight review flow, fetch only the target branch to minimize disk usage.
            if (lightweightCheckout)
            {
                var targetRefSpec = $"+refs/heads/{branchName}:refs/remotes/origin/{branchName}";
                try
                {
                    Commands.Fetch(repo, remote.Name, new[] { targetRefSpec }, fetchOptions, null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Targeted fetch for branch '{BranchName}' failed; continuing with existing refs",
                        branchName);
                }
            }
            else
            {
                Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification), fetchOptions, null);
            }

            // Clean working directory before any branch switch to avoid conflicts
            if (!lightweightCheckout)
            {
                repo.Reset(ResetMode.Hard);
                repo.RemoveUntrackedFiles();
            }

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
                // Brand new branch — create from configured base branch when available
                var configuredBase = (_options.BaseBranch ?? string.Empty).Trim();
                var baseBranchRef = !string.IsNullOrWhiteSpace(configuredBase)
                    ? repo.Branches[$"origin/{configuredBase}"]
                    : null;
                var baseCommit = baseBranchRef?.Tip ?? repo.Head.Tip;
                var baseBranchName = baseBranchRef?.FriendlyName ?? repo.Head.FriendlyName;

                if (baseCommit is null)
                {
                    throw new InvalidOperationException("Could not determine a base commit for branch creation.");
                }

                _logger.LogInformation("Creating branch '{BranchName}' from '{DefaultBranch}'",
                    branchName, baseBranchName);
                branch = repo.CreateBranch(branchName, baseCommit);
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

            if (lightweightCheckout)
            {
                repo.Refs.UpdateTarget("HEAD", branch.CanonicalName);
                _logger.LogInformation("Prepared branch '{BranchName}' in lightweight mode (full checkout skipped)", branchName);
            }
            else
            {
                Commands.Checkout(repo, branch, new CheckoutOptions
                {
                    CheckoutModifiers = CheckoutModifiers.Force
                });
                _logger.LogInformation("Checked out branch '{BranchName}'", branchName);
            }
        }

        return repoDir;
    }

    public async Task HydrateWorkingTreeAsync(string repositoryPath, IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return;
        }

        var cleanedPaths = relativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/').Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cleanedPaths.Length == 0)
        {
            return;
        }

        var quotedPaths = string.Join(" ", cleanedPaths.Select(p => $"\"{p}\""));

        await RunGitCommandAsync(repositoryPath,
            $"sparse-checkout init --no-cone",
            cancellationToken,
            ignoreExitCode: true);

        await RunGitCommandAsync(repositoryPath,
            $"sparse-checkout set --no-cone {quotedPaths}",
            cancellationToken);

        await RunGitCommandAsync(repositoryPath,
            "checkout --force",
            cancellationToken);
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

    public Task CleanupRepoAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(repositoryPath))
        {
            _logger.LogInformation("Cleaning up repo at {RepoPath}", repositoryPath);
            try { DeleteDirectory(repositoryPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete repo dir {RepoPath}", repositoryPath); }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs a shallow (depth=1) clone via the git CLI to avoid full history download.
    /// LibGit2Sharp does not support shallow clones, so we shell out for the initial clone only.
    /// </summary>
    private static async Task ShallowCloneAsync(string repoUrl, string targetDir, string username, string token, ILogger logger, bool lightweightCheckout)
    {
        // Embed credentials in the URL so git CLI can authenticate without an interactive prompt.
        var uri = new Uri(repoUrl);
        var authedUrl = $"{uri.Scheme}://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(token)}@{uri.Host}{uri.PathAndQuery}";

        var noCheckoutArg = lightweightCheckout ? " --no-checkout" : string.Empty;
        var psi = new ProcessStartInfo("git",
            $"-c credential.helper= clone --depth 1 --single-branch --filter=blob:none{noCheckoutArg} \"{authedUrl}\" \"{targetDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment =
            {
                // Prevent interactive prompts on CI/Functions hosts
                ["GIT_TERMINAL_PROMPT"] = "0",
                // Ensure credential managers do not prompt/engage in headless hosts.
                ["GCM_INTERACTIVE"] = "Never",
                // Avoid pulling LFS content during clone on constrained disks.
                ["GIT_LFS_SKIP_SMUDGE"] = "1",
            }
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git clone --depth 1 failed (exit {process.ExitCode}): {stderr.Trim()}");

        logger.LogInformation("Shallow clone complete at {TargetDir}", targetDir);
    }

    private static async Task RunGitCommandAsync(
        string repositoryPath,
        string args,
        CancellationToken cancellationToken,
        bool ignoreExitCode = false)
    {
        var psi = new ProcessStartInfo("git", $"-C \"{repositoryPath}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                ["GIT_LFS_SKIP_SMUDGE"] = "1",
            }
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 && !ignoreExitCode)
        {
            throw new InvalidOperationException($"git {args} failed (exit {process.ExitCode}): {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Deletes a directory including read-only git pack files.
    /// </summary>
    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }
        Directory.Delete(path, recursive: true);
    }

    private static string SanitizeDirectoryName(string name)
    {
        return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    private static string GetDefaultBasePath()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "data", "ado-agent-repos");
        }

        return Path.Combine(Path.GetTempPath(), "ado-agent-repos");
    }
}
