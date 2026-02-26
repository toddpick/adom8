using System.Net.Http.Headers;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// Uses GitHub REST APIs to estimate repository working-tree size without cloning.
/// </summary>
public sealed class GitHubRepositorySizingService : IRepositorySizingService
{
    private readonly GitHubOptions _gitHub;
    private readonly RepositoryCapacityOptions _options;
    private readonly ILogger<GitHubRepositorySizingService> _logger;
    private readonly HttpClient _httpClient;

    public GitHubRepositorySizingService(
        IOptions<GitHubOptions> gitHubOptions,
        IOptions<RepositoryCapacityOptions> options,
        ILogger<GitHubRepositorySizingService> logger)
    {
        _gitHub = gitHubOptions.Value;
        _options = options.Value;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _gitHub.Token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<RepositorySizingResult> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return RepositorySizingResult.Skipped("Repository capacity guardrail disabled.");
        }

        var defaultBranch = await GetDefaultBranchAsync(cancellationToken);
        var branchSha = await GetBranchShaAsync(defaultBranch, cancellationToken);
        var (fileCount, totalBytes, binaryBytes, treeTruncated) = await GetTreeStatsAsync(branchSha, cancellationToken);

        var issues = new List<string>();
        if (_options.BlockWhenTreeTruncated && treeTruncated)
        {
            issues.Add("GitHub returned a truncated tree for this repository");
        }

        if (fileCount > _options.MaxFileCount)
        {
            issues.Add($"file count {fileCount:N0} exceeds limit {_options.MaxFileCount:N0}");
        }

        if (totalBytes > _options.MaxEstimatedWorkingTreeBytes)
        {
            issues.Add($"working tree estimate {FormatBytes(totalBytes)} exceeds limit {FormatBytes(_options.MaxEstimatedWorkingTreeBytes)}");
        }

        if (binaryBytes > _options.MaxBinaryBytes)
        {
            issues.Add($"binary/docs/images estimate {FormatBytes(binaryBytes)} exceeds limit {FormatBytes(_options.MaxBinaryBytes)}");
        }

        var blocked = issues.Count > 0;
        var summary = blocked
            ? $"Repository capacity preflight blocked local clone: {string.Join("; ", issues)}. Estimated totals: {fileCount:N0} files, {FormatBytes(totalBytes)} working tree, {FormatBytes(binaryBytes)} binary/docs/images. Recommended action: use GitHub delegated coding for this story or increase hosting/storage plan."
            : $"Repository capacity preflight passed: {fileCount:N0} files, {FormatBytes(totalBytes)} working tree estimate, {FormatBytes(binaryBytes)} binary/docs/images.";

        if (blocked)
        {
            _logger.LogWarning("{Summary}", summary);
        }
        else
        {
            _logger.LogInformation("{Summary}", summary);
        }

        return new RepositorySizingResult
        {
            CanProceed = !blocked,
            CheckPerformed = true,
            TreeWasTruncated = treeTruncated,
            FileCount = fileCount,
            EstimatedWorkingTreeBytes = totalBytes,
            EstimatedBinaryBytes = binaryBytes,
            Message = summary
        };
    }

    private async Task<string> GetDefaultBranchAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"repos/{_gitHub.Owner}/{_gitHub.Repo}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("default_branch").GetString() ?? "main";
    }

    private async Task<string> GetBranchShaAsync(string branch, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"repos/{_gitHub.Owner}/{_gitHub.Repo}/branches/{branch}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("commit").GetProperty("sha").GetString()
               ?? throw new InvalidOperationException($"GitHub branch '{branch}' did not return a commit SHA.");
    }

    private async Task<(int fileCount, long totalBytes, long binaryBytes, bool truncated)> GetTreeStatsAsync(
        string sha,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/trees/{sha}?recursive=1",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var truncated = doc.RootElement.TryGetProperty("truncated", out var truncatedNode) && truncatedNode.GetBoolean();

        var binaryExtensions = new HashSet<string>(_options.BinaryExtensions, StringComparer.OrdinalIgnoreCase);

        var fileCount = 0;
        long totalBytes = 0;
        long binaryBytes = 0;

        foreach (var node in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            var type = node.GetProperty("type").GetString();
            if (!string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            fileCount++;

            var fileSize = node.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0L;
            totalBytes += fileSize;

            var path = node.GetProperty("path").GetString() ?? string.Empty;
            var ext = Path.GetExtension(path);
            if (binaryExtensions.Contains(ext))
            {
                binaryBytes += fileSize;
            }
        }

        return (fileCount, totalBytes, binaryBytes, truncated);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var value = bytes / 1024d;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

/// <summary>
/// Fallback implementation used when provider-specific sizing checks are unavailable.
/// </summary>
public sealed class NoOpRepositorySizingService : IRepositorySizingService
{
    public Task<RepositorySizingResult> EvaluateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(RepositorySizingResult.Skipped("Repository capacity preflight skipped for this repository provider."));
}
