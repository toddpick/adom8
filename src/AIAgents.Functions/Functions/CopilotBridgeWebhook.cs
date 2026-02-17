using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// HTTP-triggered Azure Function that handles GitHub webhook events for Copilot coding agent PRs.
/// 
/// When Copilot creates a PR after being assigned to a GitHub Issue, this webhook:
/// 1. Validates the GitHub webhook signature (X-Hub-Signature-256)
/// 2. Matches the PR to a pending Copilot delegation by parsing US-{id} from PR title/body
/// 3. Fetches the changed files from Copilot's PR via GitHub API
/// 4. Writes those files to the pipeline branch (feature/US-{id})
/// 5. Populates state.Artifacts.Code, records work metrics
/// 6. Closes Copilot's PR (changes are on the pipeline branch now)
/// 7. Resumes the pipeline by enqueuing the Testing agent
/// </summary>
public sealed class CopilotBridgeWebhook
{
    private readonly CopilotOptions _copilotOptions;
    private readonly GitHubOptions _githubOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IAgentTaskQueue _taskQueue;
    private readonly IActivityLogger _activityLogger;
    private readonly ILogger<CopilotBridgeWebhook> _logger;

    public CopilotBridgeWebhook(
        IOptions<CopilotOptions> copilotOptions,
        IOptions<GitHubOptions> githubOptions,
        ICopilotDelegationService delegationService,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        IAzureDevOpsClient adoClient,
        IAgentTaskQueue taskQueue,
        IActivityLogger activityLogger,
        ILogger<CopilotBridgeWebhook> logger)
    {
        _copilotOptions = copilotOptions.Value;
        _githubOptions = githubOptions.Value;
        _delegationService = delegationService;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _adoClient = adoClient;
        _taskQueue = taskQueue;
        _activityLogger = activityLogger;
        _logger = logger;
    }

    [Function("CopilotBridgeWebhook")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "copilot-webhook")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Copilot bridge webhook triggered");

        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrEmpty(body))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // Validate HMAC signature
        if (!ValidateSignature(req, body))
        {
            _logger.LogWarning("Invalid webhook signature — rejecting request");
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        // Process pull_request events and issues events (Copilot closes the issue when done)
        if (!req.Headers.TryGetValues("X-GitHub-Event", out var eventTypes))
        {
            _logger.LogDebug("No X-GitHub-Event header");
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var eventType = eventTypes.First();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // ── Issues event: Copilot closes the issue when it finishes coding ──
        if (eventType == "issues")
        {
            return await HandleIssueEventAsync(req, root, cancellationToken);
        }

        if (eventType != "pull_request")
        {
            _logger.LogDebug("Ignoring event type: {EventType}", eventType);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // ── Pull Request event: handle opened (non-draft) and ready_for_review ──
        var action = root.GetProperty("action").GetString();
        if (action is not ("opened" or "ready_for_review"))
        {
            _logger.LogDebug("Ignoring pull_request action: {Action}", action);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var pr = root.GetProperty("pull_request");
        var prNumber = pr.GetProperty("number").GetInt32();
        var prTitle = pr.GetProperty("title").GetString() ?? "";
        var prBody = pr.GetProperty("body").GetString() ?? "";
        var prBranch = pr.GetProperty("head").GetProperty("ref").GetString() ?? "";

        // Skip draft PRs — Copilot opens a draft PR as a placeholder, then
        // pushes code to it. We wait for the issue to close (Copilot finishes),
        // then process the PR at that point.
        var isDraft = pr.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
        if (isDraft)
        {
            _logger.LogInformation(
                "Ignoring draft PR #{PrNumber} — will process when Copilot closes the issue", prNumber);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        _logger.LogInformation("Processing Copilot PR #{PrNumber}: {Title}", prNumber, prTitle);

        // Try to match this PR to a pending delegation
        var workItemId = ExtractWorkItemId(prTitle, prBody);
        if (workItemId is null)
        {
            _logger.LogInformation("PR #{PrNumber} does not reference a US-{{id}} work item — ignoring", prNumber);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var delegation = await _delegationService.GetByWorkItemIdAsync(workItemId.Value, cancellationToken);
        if (delegation is null || delegation.Status != "Pending")
        {
            _logger.LogInformation(
                "No pending delegation found for WI-{WorkItemId} (PR #{PrNumber}) — ignoring",
                workItemId.Value, prNumber);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        _logger.LogInformation(
            "Matched PR #{PrNumber} to Copilot delegation for WI-{WorkItemId}",
            prNumber, workItemId.Value);

        try
        {
            await ReconcileAndResumeAsync(delegation, prNumber, prBranch, cancellationToken);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"Reconciled PR #{prNumber} for WI-{workItemId}", cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile Copilot PR #{PrNumber} for WI-{WorkItemId}",
                prNumber, workItemId.Value);

            await _activityLogger.LogAsync("Coding", workItemId.Value,
                $"Error reconciling Copilot PR #{prNumber}: {ex.Message}", "error", cancellationToken);

            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// Handles GitHub Issues events. Copilot closes the issue when it finishes coding.
    /// We detect this and then process the associated PR.
    /// </summary>
    private async Task<HttpResponseData> HandleIssueEventAsync(
        HttpRequestData req, JsonElement root, CancellationToken cancellationToken)
    {
        var action = root.GetProperty("action").GetString();
        if (action != "closed")
        {
            _logger.LogDebug("Ignoring issues action: {Action}", action);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var issue = root.GetProperty("issue");
        var issueNumber = issue.GetProperty("number").GetInt32();
        var issueTitle = issue.GetProperty("title").GetString() ?? "";

        _logger.LogInformation("Issue #{IssueNumber} closed: {Title}", issueNumber, issueTitle);

        // Check if this issue has a pending delegation
        var workItemId = ExtractWorkItemId(issueTitle, issue.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "");
        if (workItemId is null)
        {
            _logger.LogDebug("Issue #{IssueNumber} does not reference a US-{{id}} — ignoring", issueNumber);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var delegation = await _delegationService.GetByWorkItemIdAsync(workItemId.Value, cancellationToken);
        if (delegation is null || delegation.Status != "Pending")
        {
            _logger.LogDebug("No pending delegation for WI-{WorkItemId} (Issue #{IssueNumber})", workItemId, issueNumber);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        if (delegation.IssueNumber != issueNumber)
        {
            _logger.LogDebug("Issue #{IssueNumber} doesn't match delegation issue #{DelegationIssue}", issueNumber, delegation.IssueNumber);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        _logger.LogInformation(
            "Copilot finished! Issue #{IssueNumber} closed for WI-{WorkItemId} — looking for PR to reconcile",
            issueNumber, workItemId.Value);

        // Find the Copilot PR associated with this delegation
        var prInfo = await FindCopilotPrAsync(delegation, cancellationToken);
        if (prInfo is null)
        {
            _logger.LogWarning("No PR found for Copilot delegation WI-{WorkItemId}. Will wait for timeout checker.", workItemId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            await _activityLogger.LogAsync("Coding", workItemId.Value,
                $"Copilot finished coding (Issue #{issueNumber} closed) — reconciling PR #{prInfo.Value.Number}", "info", cancellationToken);

            await ReconcileAndResumeAsync(delegation, prInfo.Value.Number, prInfo.Value.Branch, cancellationToken);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"Reconciled Copilot PR #{prInfo.Value.Number} for WI-{workItemId}", cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile after Copilot finished WI-{WorkItemId}", workItemId);
            await _activityLogger.LogAsync("Coding", workItemId.Value,
                $"Error reconciling Copilot PR: {ex.Message}", "error", cancellationToken);
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// Finds the Copilot PR for a delegation by searching open/closed PRs from Copilot branches.
    /// </summary>
    private async Task<(int Number, string Branch)?> FindCopilotPrAsync(
        CopilotDelegation delegation, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient();

        // Search for PRs targeting the pipeline branch or main
        var searchUri = $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls?state=all&sort=created&direction=desc&per_page=10";
        var response = await httpClient.GetAsync(searchUri, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var prs = JsonDocument.Parse(json);

        foreach (var pr in prs.RootElement.EnumerateArray())
        {
            var prTitle = pr.GetProperty("title").GetString() ?? "";
            var prBody = pr.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";
            var prCreated = pr.GetProperty("created_at").GetDateTime();

            // Must be created after delegation and reference the same work item
            if (prCreated < delegation.DelegatedAt.AddMinutes(-1)) continue;

            var prWorkItemId = ExtractWorkItemId(prTitle, prBody);
            if (prWorkItemId != delegation.WorkItemId) continue;

            var prNumber = pr.GetProperty("number").GetInt32();
            var prBranch = pr.GetProperty("head").GetProperty("ref").GetString() ?? "";
            var prFiles = pr.GetProperty("changed_files").GetInt32();

            if (prFiles > 0)
            {
                _logger.LogInformation("Found Copilot PR #{PrNumber} with {Files} files for WI-{WorkItemId}",
                    prNumber, prFiles, delegation.WorkItemId);
                return (prNumber, prBranch);
            }
        }

        return null;
    }

    /// <summary>
    /// Reconciles Copilot's PR changes onto the pipeline branch and resumes the pipeline.
    /// </summary>
    private async Task ReconcileAndResumeAsync(
        CopilotDelegation delegation,
        int prNumber,
        string copilotBranch,
        CancellationToken cancellationToken)
    {
        var branchName = delegation.BranchName;
        var workItemId = delegation.WorkItemId;

        await _activityLogger.LogAsync("Coding", workItemId,
            $"Copilot PR #{prNumber} received — reconciling changes", "info", cancellationToken);

        // Fetch PR file list and metrics from GitHub API
        var (changedFiles, metrics) = await FetchPrDetailsAsync(prNumber, delegation.DelegatedAt, cancellationToken);

        // Safety check: don't reconcile if Copilot hasn't pushed any actual file changes yet.
        // This prevents closing the issue and resuming the pipeline with 0 code changes.
        if (changedFiles.Count == 0)
        {
            _logger.LogInformation(
                "PR #{PrNumber} for WI-{WorkItemId} has no changed files yet — skipping reconciliation",
                prNumber, workItemId);
            await _activityLogger.LogAsync("Coding", workItemId,
                $"Copilot PR #{prNumber} has no file changes yet — waiting for code", "info", cancellationToken);
            return;
        }

        // Check out the pipeline branch
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        // Fetch each changed file from Copilot's branch and write to pipeline branch
        var reconciledFiles = new List<string>();
        foreach (var file in changedFiles)
        {
            if (file.Status == "removed")
                continue; // Don't delete files through reconciliation

            var content = await FetchFileContentAsync(file.Filename, copilotBranch, cancellationToken);
            if (content is not null)
            {
                await _gitOps.WriteFileAsync(repoPath, file.Filename, content, cancellationToken);
                reconciledFiles.Add(file.Filename);
            }
        }

        // Update state.json
        await using var context = _contextFactory.Create(workItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);

        foreach (var file in reconciledFiles)
            state.Artifacts.Code.Add(file);

        // Record Copilot metrics as a special token usage entry
        state.TokenUsage.RecordUsage("Coding", new TokenUsageData
        {
            InputTokens = 0,
            OutputTokens = 0,
            TotalTokens = 0,
            EstimatedCost = 0m,
            Model = "copilot-coding-agent"
        });

        state.Agents["Coding"] = AgentStatus.Completed();
        state.Agents["Coding"].AdditionalData = new Dictionary<string, object>
        {
            ["mode"] = "copilot",
            ["copilotPrNumber"] = prNumber,
            ["issueNumber"] = delegation.IssueNumber,
            ["filesChanged"] = metrics.FilesChanged,
            ["linesAdded"] = metrics.LinesAdded,
            ["linesDeleted"] = metrics.LinesDeleted,
            ["durationMinutes"] = metrics.DurationMinutes,
            ["commitCount"] = metrics.CommitCount
        };
        state.CurrentState = "AI Test";
        await context.SaveStateAsync(state, cancellationToken);

        // Commit reconciled changes
        await _gitOps.CommitAndPushAsync(repoPath,
            $"[AI Coding] US-{workItemId}: Copilot implementation (PR #{prNumber}, {reconciledFiles.Count} files)",
            cancellationToken);

        // Close Copilot's PR if configured
        if (_copilotOptions.AutoCloseCopilotPr)
        {
            await ClosePullRequestAsync(prNumber,
                $"Changes incorporated into pipeline branch `{branchName}`. Pipeline continuing via ADO-Agent.",
                cancellationToken);
        }

        // Close the GitHub Issue if one was created
        if (delegation.IssueNumber > 0)
        {
            await CloseIssueAsync(delegation.IssueNumber, cancellationToken);
        }

        // Update delegation record
        delegation.Status = "Completed";
        delegation.CopilotPrNumber = prNumber;
        delegation.CompletedAt = DateTime.UtcNow;
        await _delegationService.UpdateAsync(delegation, cancellationToken);

        // Update ADO work item
        try { await _adoClient.UpdateWorkItemFieldAsync(workItemId, CustomFieldNames.Paths.LastAgent, "Coding", cancellationToken); }
        catch { /* field may not exist yet */ }

        await _adoClient.UpdateWorkItemStateAsync(workItemId, "AI Test", cancellationToken);

        await _adoClient.AddWorkItemCommentAsync(workItemId,
            $"<b>🤖 AI Coding Agent Complete (GitHub Copilot)</b><br/>" +
            $"Strategy: Copilot coding agent<br/>" +
            $"PR: #{prNumber} | Files: {metrics.FilesChanged} | +{metrics.LinesAdded}/-{metrics.LinesDeleted} lines<br/>" +
            $"Duration: {metrics.DurationMinutes:F1} minutes | Commits: {metrics.CommitCount}",
            cancellationToken);

        // Enqueue Testing agent to resume pipeline
        var nextTask = new AgentTask
        {
            WorkItemId = workItemId,
            AgentType = AgentType.Testing,
            CorrelationId = delegation.CorrelationId
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        var tokensForLog = 0;
        var costForLog = 0m;
        await _activityLogger.LogAsync("Coding", workItemId,
            $"Copilot coding complete — {metrics.FilesChanged} files, +{metrics.LinesAdded}/-{metrics.LinesDeleted} lines, {metrics.DurationMinutes:F1}m. Pipeline resumed.",
            tokensForLog, costForLog, "info", cancellationToken);

        _logger.LogInformation(
            "Copilot bridge reconciled PR #{PrNumber} for WI-{WorkItemId} — {Files} files, pipeline resumed",
            prNumber, workItemId, reconciledFiles.Count);
    }

    /// <summary>
    /// Extracts <c>US-{id}</c> work item ID from PR title or body.
    /// </summary>
    internal static int? ExtractWorkItemId(string title, string body)
    {
        var combined = $"{title}\n{body}";
        var match = System.Text.RegularExpressions.Regex.Match(combined, @"US-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// Validates the GitHub webhook HMAC-SHA256 signature.
    /// </summary>
    private bool ValidateSignature(HttpRequestData req, string body)
    {
        if (string.IsNullOrEmpty(_copilotOptions.WebhookSecret))
        {
            _logger.LogWarning("Copilot:WebhookSecret not configured — skipping signature validation");
            return true; // Allow if not configured (development mode)
        }

        if (!req.Headers.TryGetValues("X-Hub-Signature-256", out var signatures))
        {
            return false;
        }

        var signature = signatures.First();
        if (!signature.StartsWith("sha256="))
        {
            return false;
        }

        var expectedHash = signature["sha256=".Length..];
        var keyBytes = Encoding.UTF8.GetBytes(_copilotOptions.WebhookSecret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = BitConverter.ToString(hmac.ComputeHash(bodyBytes)).Replace("-", "").ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHash),
            Encoding.UTF8.GetBytes(expectedHash));
    }

    /// <summary>
    /// Fetches PR changed files and metrics from GitHub API.
    /// </summary>
    private async Task<(List<PrFile> Files, CopilotMetrics Metrics)> FetchPrDetailsAsync(
        int prNumber, DateTime delegatedAt, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient();

        // Get PR metadata
        var prResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}",
            cancellationToken);
        prResponse.EnsureSuccessStatusCode();
        var prJson = await prResponse.Content.ReadAsStringAsync(cancellationToken);
        using var prDoc = JsonDocument.Parse(prJson);
        var prRoot = prDoc.RootElement;

        var additions = prRoot.GetProperty("additions").GetInt32();
        var deletions = prRoot.GetProperty("deletions").GetInt32();
        var changedFileCount = prRoot.GetProperty("changed_files").GetInt32();
        var commits = prRoot.GetProperty("commits").GetInt32();

        // Get individual file list
        var filesResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}/files?per_page=100",
            cancellationToken);
        filesResponse.EnsureSuccessStatusCode();
        var filesJson = await filesResponse.Content.ReadAsStringAsync(cancellationToken);
        using var filesDoc = JsonDocument.Parse(filesJson);

        var files = new List<PrFile>();
        foreach (var file in filesDoc.RootElement.EnumerateArray())
        {
            files.Add(new PrFile
            {
                Filename = file.GetProperty("filename").GetString() ?? "",
                Status = file.GetProperty("status").GetString() ?? ""
            });
        }

        var duration = (DateTime.UtcNow - delegatedAt).TotalMinutes;

        var metrics = new CopilotMetrics
        {
            PullRequestNumber = prNumber,
            FilesChanged = changedFileCount,
            LinesAdded = additions,
            LinesDeleted = deletions,
            DurationMinutes = duration,
            CommitCount = commits
        };

        return (files, metrics);
    }

    /// <summary>
    /// Fetches raw file content from a specific branch via GitHub API.
    /// </summary>
    private async Task<string?> FetchFileContentAsync(
        string filePath, string branch, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient();
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        var encodedPath = Uri.EscapeDataString(filePath);
        var response = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/contents/{encodedPath}?ref={branch}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch {File} from branch {Branch}: {Status}",
                filePath, branch, response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Closes a PR with a comment.
    /// </summary>
    private async Task ClosePullRequestAsync(int prNumber, string comment, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient();

        // Add comment
        var commentBody = JsonSerializer.Serialize(new { body = comment });
        var commentContent = new StringContent(commentBody, Encoding.UTF8, "application/json");
        await httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{prNumber}/comments",
            commentContent, cancellationToken);

        // Close PR
        var closeBody = JsonSerializer.Serialize(new { state = "closed" });
        var closeContent = new StringContent(closeBody, Encoding.UTF8, "application/json");
        await httpClient.PatchAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}",
            closeContent, cancellationToken);

        _logger.LogInformation("Closed Copilot PR #{PrNumber}", prNumber);
    }

    /// <summary>
    /// Closes a GitHub Issue with a completion label.
    /// </summary>
    private async Task CloseIssueAsync(int issueNumber, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient();

        var closeBody = JsonSerializer.Serialize(new
        {
            state = "closed",
            labels = new[] { "copilot-completed" }
        });
        var closeContent = new StringContent(closeBody, Encoding.UTF8, "application/json");
        await httpClient.PatchAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}",
            closeContent, cancellationToken);

        _logger.LogInformation("Closed GitHub Issue #{IssueNumber} (copilot-completed)", issueNumber);
    }

    private HttpClient CreateGitHubClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _githubOptions.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed record PrFile
    {
        public required string Filename { get; init; }
        public required string Status { get; init; }
    }
}
