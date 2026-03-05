using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// GitHub REST API-based implementation of repository context operations.
/// Provides file tree listing, file content fetching, and atomic multi-file commits
/// without requiring a local git clone.
/// Follows the same authentication and error handling pattern as GitHubCodebaseOnboardingService.
/// </summary>
public sealed class GitHubApiContextService : IGitHubApiContextService
{
    private readonly GitHubOptions _gitHub;
    private readonly ILogger<GitHubApiContextService> _logger;
    private readonly HttpClient _httpClient;

    public GitHubApiContextService(
        IOptions<GitHubOptions> gitHubOptions,
        ILogger<GitHubApiContextService> logger,
        HttpClient? httpClient = null)
    {
        _gitHub = gitHubOptions.Value;
        _logger = logger;

        _httpClient = httpClient ?? CreateDefaultHttpClient(_gitHub);
    }

    private static HttpClient CreateDefaultHttpClient(GitHubOptions gitHub)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(90)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gitHub.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct = default)
    {
        var headSha = await GetBranchHeadShaAsync(branch, ct);

        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/trees/{headSha}?recursive=1",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var results = new List<string>();
        foreach (var node in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            var type = node.GetProperty("type").GetString() ?? string.Empty;
            if (!string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = node.GetProperty("path").GetString();
            if (!string.IsNullOrEmpty(path))
                results.Add(path);
        }

        _logger.LogDebug("GetFileTreeAsync: {Count} files on branch {Branch}", results.Count, branch);
        return results;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string?>> GetFileContentsAsync(
        string branch,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Fetch files in parallel with bounded concurrency to respect GitHub rate limits
        using var semaphore = new SemaphoreSlim(5, 5);
        var tasks = paths.Select(async path =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                result[path] = await TryGetFileContentAsync(path, branch, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    /// <inheritdoc/>
    public async Task<string> GetRecentCommitSummaryAsync(string branch, int count = 20, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/commits?sha={Uri.EscapeDataString(branch)}&per_page={count}",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        foreach (var commit in doc.RootElement.EnumerateArray())
        {
            var sha = commit.GetProperty("sha").GetString()?[..7] ?? "???????";
            var message = commit.GetProperty("commit").GetProperty("message").GetString() ?? string.Empty;
            var firstLine = message.Split('\n')[0];
            sb.AppendLine($"- {sha} {firstLine}");
        }
        return sb.ToString();
    }

    /// <inheritdoc/>
    public async Task WriteFileAsync(string branch, string path, string content, string commitMessage, CancellationToken ct = default)
    {
        var existingSha = await TryGetFileShaAsync(path, branch, ct);

        var payload = new Dictionary<string, object?>
        {
            ["message"] = commitMessage,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch
        };
        if (existingSha is not null)
            payload["sha"] = existingSha;

        var json = JsonSerializer.Serialize(payload);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/");

        var response = await _httpClient.PutAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/contents/{encodedPath}",
            httpContent,
            ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("WriteFileAsync: wrote {Path} on {Branch}", path, branch);
    }

    /// <inheritdoc/>
    public async Task WriteFilesAsync(
        string branch,
        IReadOnlyDictionary<string, string> files,
        string commitMessage,
        CancellationToken ct = default)
    {
        if (files.Count == 0)
            return;

        // Step 1: Get current branch head SHA
        var headSha = await GetBranchHeadShaAsync(branch, ct);

        // Step 2: Get the tree SHA from the commit
        var commitResponse = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/commits/{headSha}",
            ct);
        var commitBody = await commitResponse.Content.ReadAsStringAsync(ct);
        commitResponse.EnsureSuccessStatusCode();
        using var commitDoc = JsonDocument.Parse(commitBody);
        var baseTreeSha = commitDoc.RootElement.GetProperty("tree").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("Could not resolve base tree SHA.");

        // Step 3: Create new tree with updated files
        var treeItems = files.Select(kvp => new
        {
            path = kvp.Key,
            mode = "100644",
            type = "blob",
            content = kvp.Value
        }).ToArray();

        var treePayload = JsonSerializer.Serialize(new
        {
            base_tree = baseTreeSha,
            tree = treeItems
        });

        var treeResponse = await _httpClient.PostAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/trees",
            new StringContent(treePayload, Encoding.UTF8, "application/json"),
            ct);
        var treeBody = await treeResponse.Content.ReadAsStringAsync(ct);
        treeResponse.EnsureSuccessStatusCode();
        using var treeDoc = JsonDocument.Parse(treeBody);
        var newTreeSha = treeDoc.RootElement.GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("Could not get new tree SHA.");

        // Step 4: Create new commit
        var newCommitPayload = JsonSerializer.Serialize(new
        {
            message = commitMessage,
            tree = newTreeSha,
            parents = new[] { headSha }
        });

        var newCommitResponse = await _httpClient.PostAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/commits",
            new StringContent(newCommitPayload, Encoding.UTF8, "application/json"),
            ct);
        var newCommitBody = await newCommitResponse.Content.ReadAsStringAsync(ct);
        newCommitResponse.EnsureSuccessStatusCode();
        using var newCommitDoc = JsonDocument.Parse(newCommitBody);
        var newCommitSha = newCommitDoc.RootElement.GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("Could not get new commit SHA.");

        // Step 5: Update branch ref to point to new commit
        var updateRefPayload = JsonSerializer.Serialize(new
        {
            sha = newCommitSha,
            force = false
        });

        var updateRefResponse = await _httpClient.PatchAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/refs/heads/{Uri.EscapeDataString(branch)}",
            new StringContent(updateRefPayload, Encoding.UTF8, "application/json"),
            ct);
        var updateRefBody = await updateRefResponse.Content.ReadAsStringAsync(ct);
        updateRefResponse.EnsureSuccessStatusCode();

        _logger.LogInformation(
            "WriteFilesAsync: committed {Count} files to {Branch} (commit {Sha})",
            files.Count, branch, newCommitSha[..7]);
    }

    private async Task<string> GetBranchHeadShaAsync(string branch, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/branches/{Uri.EscapeDataString(branch)}",
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("commit").GetProperty("sha").GetString()
               ?? throw new InvalidOperationException($"Could not resolve head SHA for branch '{branch}'.");
    }

    private async Task<string?> TryGetFileContentAsync(string path, string branch, CancellationToken ct)
    {
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/");
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(branch)}",
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var contentNode))
            return null;

        var base64 = contentNode.GetString();
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        var normalized = base64.Replace("\n", string.Empty).Replace("\r", string.Empty);
        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<string?> TryGetFileShaAsync(string path, string branch, CancellationToken ct)
    {
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/");
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(branch)}",
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("sha", out var shaNode) ? shaNode.GetString() : null;
    }
}
