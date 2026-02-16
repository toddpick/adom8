using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIAgents.Core.Services;

/// <summary>
/// <see cref="IRepositoryProvider"/> implementation for GitHub.
/// Uses the GitHub REST API v3 with a classic Personal Access Token.
/// </summary>
public sealed class GitHubRepositoryProvider : IRepositoryProvider
{
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubRepositoryProvider> _logger;
    private readonly HttpClient _httpClient;

    private string Owner => _options.Owner;
    private string Repo => _options.Repo;

    public GitHubRepositoryProvider(
        IOptions<GitHubOptions> options,
        ILogger<GitHubRepositoryProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.Token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<int> CreatePullRequestAsync(
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating GitHub PR: {Source} → {Target} in {Owner}/{Repo}",
            sourceBranch, targetBranch, Owner, Repo);

        var requestBody = new
        {
            title,
            body = description,
            head = sourceBranch,
            @base = targetBranch
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{Owner}/{Repo}/pulls", content, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // GitHub returns 422 when a PR already exists for the same head→base
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity
                && responseBody.Contains("A pull request already exists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "PR already exists for {Source} → {Target}, finding existing PR",
                    sourceBranch, targetBranch);

                var existingPr = await FindExistingPullRequestAsync(
                    sourceBranch, targetBranch, cancellationToken);
                if (existingPr.HasValue)
                {
                    _logger.LogInformation("Found existing GitHub PR #{PrNumber}", existingPr.Value);
                    return existingPr.Value;
                }
            }

            _logger.LogError(
                "GitHub PR creation failed ({StatusCode}): {Body}",
                response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode(); // throws
        }

        using var doc = JsonDocument.Parse(responseBody);
        var prNumber = doc.RootElement.GetProperty("number").GetInt32();

        _logger.LogInformation("Created GitHub PR #{PrNumber}", prNumber);
        return prNumber;
    }

    public async Task MergePullRequestAsync(
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Merging GitHub PR #{PrNumber} in {Owner}/{Repo}",
            pullRequestId, Owner, Repo);

        var requestBody = new
        {
            merge_method = "squash"
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PutAsync(
            $"repos/{Owner}/{Repo}/pulls/{pullRequestId}/merge", content, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "GitHub PR merge failed ({StatusCode}): {Body}",
                response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("GitHub PR #{PrNumber} merged (squash)", pullRequestId);
    }

    public async Task<int> TriggerDeploymentAsync(
        string branch,
        CancellationToken cancellationToken = default)
    {
        var workflow = _options.DeployWorkflow
            ?? throw new InvalidOperationException(
                "GitHub:DeployWorkflow must be configured for Level 5 autonomy. " +
                "Set it to the filename of a GitHub Actions workflow with workflow_dispatch trigger (e.g., 'deploy.yml').");

        _logger.LogInformation(
            "Triggering GitHub Actions workflow '{Workflow}' on branch '{Branch}' in {Owner}/{Repo}",
            workflow, branch, Owner, Repo);

        var requestBody = new
        {
            @ref = branch,
            inputs = new Dictionary<string, string>
            {
                ["triggered_by"] = "ai-agent-pipeline"
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{Owner}/{Repo}/actions/workflows/{workflow}/dispatches",
            content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "GitHub workflow dispatch failed ({StatusCode}): {Body}",
                response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        // workflow_dispatch returns 204 No Content — no run ID in response.
        // Query the most recent run to get the ID.
        var runId = await GetLatestWorkflowRunIdAsync(workflow, branch, cancellationToken);

        _logger.LogInformation("GitHub Actions workflow dispatched. Run #{RunId}", runId);
        return runId;
    }

    /// <summary>
    /// After a workflow_dispatch, GitHub doesn't return the run ID directly.
    /// Poll for the most recent run on this workflow + branch (best-effort).
    /// </summary>
    private async Task<int> GetLatestWorkflowRunIdAsync(
        string workflow, string branch, CancellationToken cancellationToken)
    {
        // Brief delay to let GitHub register the run
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        var response = await _httpClient.GetAsync(
            $"repos/{Owner}/{Repo}/actions/workflows/{workflow}/runs?branch={branch}&per_page=1",
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var runs = doc.RootElement.GetProperty("workflow_runs");
            if (runs.GetArrayLength() > 0)
            {
                return runs[0].GetProperty("id").GetInt32();
            }
        }

        _logger.LogWarning("Could not determine workflow run ID after dispatch — returning -1");
        return -1;
    }

    /// <summary>
    /// Find an existing open PR for the given source → target branch.
    /// </summary>
    private async Task<int?> FindExistingPullRequestAsync(
        string sourceBranch, string targetBranch, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"repos/{Owner}/{Repo}/pulls?state=open&head={Owner}:{sourceBranch}&base={targetBranch}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var pulls = doc.RootElement;

        if (pulls.GetArrayLength() > 0)
            return pulls[0].GetProperty("number").GetInt32();

        return null;
    }
}
