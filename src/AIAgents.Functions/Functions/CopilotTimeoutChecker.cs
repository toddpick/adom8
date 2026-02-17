using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Timer-triggered Azure Function that checks for completed or timed-out Copilot delegations.
/// 
/// Runs every 2 minutes. For each pending delegation:
/// 1. Checks if Copilot has created a PR with commits (including draft PRs).
///    If found, performs full file reconciliation from Copilot's PR branch onto the
///    pipeline branch, then marks the delegation complete and enqueues the Testing agent.
/// 2. If no completed PR is found and the delegation exceeds
///    <see cref="CopilotOptions.TimeoutMinutes"/>, falls back to the built-in agentic
///    coding loop.
/// 
/// This is the primary mechanism for detecting Copilot completion since Copilot creates
/// draft PRs (which the webhook ignores) and does not close its own GitHub Issues.
/// </summary>
public sealed class CopilotTimeoutChecker
{
    private readonly CopilotOptions _copilotOptions;
    private readonly GitHubOptions _githubOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly IAgentTaskQueue _taskQueue;
    private readonly IActivityLogger _activityLogger;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ILogger<CopilotTimeoutChecker> _logger;

    public CopilotTimeoutChecker(
        IOptions<CopilotOptions> copilotOptions,
        IOptions<GitHubOptions> githubOptions,
        ICopilotDelegationService delegationService,
        IAgentTaskQueue taskQueue,
        IActivityLogger activityLogger,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ILogger<CopilotTimeoutChecker> logger)
    {
        _copilotOptions = copilotOptions.Value;
        _githubOptions = githubOptions.Value;
        _delegationService = delegationService;
        _taskQueue = taskQueue;
        _activityLogger = activityLogger;
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs every 2 minutes to check for completed or timed-out Copilot delegations.
    /// First checks if Copilot has finished (PR exists with commits, including drafts)
    /// and performs full file reconciliation + pipeline resumption inline.
    /// If truly timed out, falls back to the built-in agentic loop.
    /// </summary>
    [Function("CopilotTimeoutChecker")]
    public async Task RunAsync(
        [TimerTrigger("0 */2 * * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        if (!_copilotOptions.Enabled)
        {
            return; // Copilot not enabled — nothing to check
        }

        // First, check ALL pending delegations for completion (Copilot doesn't close its own issues)
        var pending = await _delegationService.GetPendingAsync(cancellationToken);
        foreach (var delegation in pending)
        {
            try
            {
                await CheckForCompletionAsync(delegation, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to check completion for Copilot delegation WI-{WorkItemId}",
                    delegation.WorkItemId);
            }
        }

        // Then check for actual timeouts (delegation may have been completed above via webhook)
        var timeout = TimeSpan.FromMinutes(_copilotOptions.TimeoutMinutes);
        var timedOut = await _delegationService.GetTimedOutAsync(timeout, cancellationToken);

        if (timedOut.Count == 0)
        {
            return;
        }

        _logger.LogWarning("{Count} Copilot delegation(s) timed out — falling back to agentic loop", timedOut.Count);

        foreach (var delegation in timedOut)
        {
            try
            {
                await HandleTimeoutAsync(delegation, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to handle timeout for Copilot delegation WI-{WorkItemId}",
                    delegation.WorkItemId);
            }
        }
    }

    /// <summary>
    /// Checks if Copilot has finished coding by looking for a PR with commits.
    /// Copilot doesn't close its GitHub Issue when done, so we detect completion
    /// by finding a matching PR (including drafts) and auto-closing the issue.
    /// When a completed PR is found, reconciles files and resumes the pipeline inline.
    /// </summary>
    private async Task CheckForCompletionAsync(CopilotDelegation delegation, CancellationToken cancellationToken)
    {
        var elapsed = (DateTime.UtcNow - delegation.DelegatedAt).TotalMinutes;

        // Don't check too early — give Copilot at least 3 minutes to start
        if (elapsed < 3)
        {
            return;
        }

        _logger.LogInformation(
            "Checking Copilot completion for WI-{WorkItemId} (Issue #{IssueNumber}, {Elapsed:F0}m elapsed)",
            delegation.WorkItemId, delegation.IssueNumber, elapsed);

        using var httpClient = CreateGitHubClient();

        // Check if the issue is already closed (webhook should handle it, but may have missed)
        var issueResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{delegation.IssueNumber}",
            cancellationToken);
        if (!issueResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch Issue #{IssueNumber} for WI-{WorkItemId} (status {StatusCode})",
                delegation.IssueNumber, delegation.WorkItemId, issueResponse.StatusCode);
            return;
        }

        var issueJson = await issueResponse.Content.ReadAsStringAsync(cancellationToken);
        using var issueDoc = JsonDocument.Parse(issueJson);
        var issueState = issueDoc.RootElement.GetProperty("state").GetString();

        if (issueState == "closed")
        {
            _logger.LogInformation(
                "Issue #{IssueNumber} already closed for WI-{WorkItemId} but delegation still Pending — completing directly (webhook may have missed it)",
                delegation.IssueNumber, delegation.WorkItemId);

            // Find the PR so we can record its number, then complete the delegation ourselves
            await FindPrAndCompleteDelegation(httpClient, delegation, cancellationToken);
            return;
        }

        // Look for a PR created by Copilot for this work item (includes draft PRs)
        var prResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls?state=all&sort=created&direction=desc&per_page=20",
            cancellationToken);
        if (!prResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch PRs for WI-{WorkItemId} (status {StatusCode})",
                delegation.WorkItemId, prResponse.StatusCode);
            return;
        }

        var prJson = await prResponse.Content.ReadAsStringAsync(cancellationToken);
        using var prDoc = JsonDocument.Parse(prJson);

        foreach (var pr in prDoc.RootElement.EnumerateArray())
        {
            var prTitle = pr.GetProperty("title").GetString() ?? "";
            var prBody = pr.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";
            var prCreated = pr.GetProperty("created_at").GetDateTime();
            var prBaseBranch = pr.TryGetProperty("base", out var baseProp)
                ? baseProp.TryGetProperty("ref", out var baseRef) ? baseRef.GetString() ?? "" : ""
                : "";

            if (prCreated < delegation.DelegatedAt.AddMinutes(-1)) continue;

            // Check if this PR references our work item via title, body, or base branch
            var matchesById = prTitle.Contains($"US-{delegation.WorkItemId}", StringComparison.OrdinalIgnoreCase) ||
                              prBody.Contains($"US-{delegation.WorkItemId}", StringComparison.OrdinalIgnoreCase) ||
                              prTitle.Contains(delegation.WorkItemId.ToString());
            var matchesByIssue = delegation.IssueNumber > 0 &&
                                 prBody.Contains($"#{delegation.IssueNumber}");
            var matchesByBranch = !string.IsNullOrEmpty(delegation.BranchName) &&
                                  string.Equals(prBaseBranch, delegation.BranchName, StringComparison.OrdinalIgnoreCase);

            if (!matchesById && !matchesByIssue && !matchesByBranch)
            {
                continue;
            }

            var prNumber = pr.GetProperty("number").GetInt32();
            var isDraft = pr.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();

            _logger.LogInformation(
                "Found candidate PR #{PrNumber} for WI-{WorkItemId} (draft={IsDraft}, title=\"{Title}\", matchById={ById}, matchByIssue={ByIssue}, matchByBranch={ByBranch})",
                prNumber, delegation.WorkItemId, isDraft, prTitle, matchesById, matchesByIssue, matchesByBranch);

            // The PR list endpoint does NOT return 'commits' — must fetch individual PR detail
            var prDetailResponse = await httpClient.GetAsync(
                $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}",
                cancellationToken);
            if (!prDetailResponse.IsSuccessStatusCode) continue;

            var prDetailJson = await prDetailResponse.Content.ReadAsStringAsync(cancellationToken);
            using var prDetailDoc = JsonDocument.Parse(prDetailJson);
            var prCommits = prDetailDoc.RootElement.GetProperty("commits").GetInt32();

            _logger.LogInformation(
                "PR #{PrNumber} detail: {Commits} commits, draft={IsDraft} for WI-{WorkItemId}",
                prNumber, prCommits, isDraft, delegation.WorkItemId);

            if (prCommits > 0)
            {
                _logger.LogInformation(
                    "Copilot completed! PR #{PrNumber} has {Commits} commits for WI-{WorkItemId} (Issue #{IssueNumber}) — reconciling and completing inline",
                    prNumber, prCommits, delegation.WorkItemId, delegation.IssueNumber);

                // Close the issue so it won't trigger again
                var closeBody = JsonSerializer.Serialize(new { state = "closed" });
                var closeContent = new StringContent(closeBody, Encoding.UTF8, "application/json");
                await httpClient.PatchAsync(
                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{delegation.IssueNumber}",
                    closeContent, cancellationToken);

                // Complete delegation with full file reconciliation and enqueue next agent
                await CompleteDelegationAndResume(delegation, prNumber, prCommits, cancellationToken);
                return;
            }
        }

        _logger.LogInformation("No completed PR found yet for WI-{WorkItemId} ({Elapsed:F0}m elapsed)",
            delegation.WorkItemId, elapsed);
    }

    /// <summary>
    /// Searches for a matching PR and completes the delegation directly.
    /// Called when the issue is already closed but the delegation is still Pending.
    /// </summary>
    private async Task FindPrAndCompleteDelegation(HttpClient httpClient, CopilotDelegation delegation, CancellationToken cancellationToken)
    {
        var prResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls?state=all&sort=created&direction=desc&per_page=20",
            cancellationToken);
        if (!prResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch PRs for WI-{WorkItemId} fallback completion", delegation.WorkItemId);
            return;
        }

        var prJson = await prResponse.Content.ReadAsStringAsync(cancellationToken);
        using var prDoc = JsonDocument.Parse(prJson);

        foreach (var pr in prDoc.RootElement.EnumerateArray())
        {
            var prTitle = pr.GetProperty("title").GetString() ?? "";
            var prBody = pr.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";
            var prCreated = pr.GetProperty("created_at").GetDateTime();
            var prBaseBranch = pr.TryGetProperty("base", out var baseProp)
                ? baseProp.TryGetProperty("ref", out var baseRef) ? baseRef.GetString() ?? "" : ""
                : "";

            if (prCreated < delegation.DelegatedAt.AddMinutes(-1)) continue;

            // Match by work item ID, issue number reference, or base branch
            if (!prTitle.Contains($"US-{delegation.WorkItemId}", StringComparison.OrdinalIgnoreCase) &&
                !prBody.Contains($"US-{delegation.WorkItemId}", StringComparison.OrdinalIgnoreCase) &&
                !prTitle.Contains(delegation.WorkItemId.ToString()) &&
                !(delegation.IssueNumber > 0 && prBody.Contains($"#{delegation.IssueNumber}")) &&
                !(!string.IsNullOrEmpty(delegation.BranchName) &&
                  string.Equals(prBaseBranch, delegation.BranchName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var prNumber = pr.GetProperty("number").GetInt32();
            var prCommits = 0;

            // Fetch PR detail for commit count
            var prDetailResponse = await httpClient.GetAsync(
                $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}",
                cancellationToken);
            if (prDetailResponse.IsSuccessStatusCode)
            {
                var prDetailJson = await prDetailResponse.Content.ReadAsStringAsync(cancellationToken);
                using var prDetailDoc = JsonDocument.Parse(prDetailJson);
                prCommits = prDetailDoc.RootElement.GetProperty("commits").GetInt32();
            }

            _logger.LogInformation(
                "Found PR #{PrNumber} for WI-{WorkItemId} — completing delegation directly",
                prNumber, delegation.WorkItemId);

            // Complete delegation and enqueue next agent — fully inline
            await CompleteDelegationAndResume(delegation, prNumber, prCommits, cancellationToken);
            return;
        }

        _logger.LogWarning(
            "Issue #{IssueNumber} closed but no matching PR found for WI-{WorkItemId} — completing delegation as-is",
            delegation.IssueNumber, delegation.WorkItemId);

        // Even without a PR, the issue is closed so complete the delegation to prevent infinite stuck state
        await CompleteDelegationAndResume(delegation, 0, 0, cancellationToken);
    }

    /// <summary>
    /// Completes a Copilot delegation end-to-end: reconciles code from Copilot's PR branch
    /// onto the pipeline branch, marks delegation done, updates ADO state, and enqueues
    /// the next agent. Does NOT rely on any webhook — fully self-contained.
    /// </summary>
    private async Task CompleteDelegationAndResume(CopilotDelegation delegation, int prNumber, int prCommits, CancellationToken cancellationToken)
    {
        var elapsed = (DateTime.UtcNow - delegation.DelegatedAt).TotalMinutes;
        var reconciledFileCount = 0;
        var linesAdded = 0;
        var linesDeleted = 0;

        // 1. Reconcile files from Copilot's PR onto the pipeline branch
        if (prNumber > 0)
        {
            try
            {
                using var httpClient = CreateGitHubClient();

                // Fetch PR detail to get the source branch name and diff stats
                var prDetailResponse = await httpClient.GetAsync(
                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}",
                    cancellationToken);

                if (prDetailResponse.IsSuccessStatusCode)
                {
                    var prDetailJson = await prDetailResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var prDetailDoc = JsonDocument.Parse(prDetailJson);
                    var prRoot = prDetailDoc.RootElement;
                    var copilotBranch = prRoot.GetProperty("head").GetProperty("ref").GetString() ?? "";
                    linesAdded = prRoot.GetProperty("additions").GetInt32();
                    linesDeleted = prRoot.GetProperty("deletions").GetInt32();

                    if (!string.IsNullOrEmpty(copilotBranch))
                    {
                        // Fetch changed file list from the PR
                        var filesResponse = await httpClient.GetAsync(
                            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}/files?per_page=100",
                            cancellationToken);

                        if (filesResponse.IsSuccessStatusCode)
                        {
                            var filesJson = await filesResponse.Content.ReadAsStringAsync(cancellationToken);
                            using var filesDoc = JsonDocument.Parse(filesJson);

                            // Check out the pipeline branch
                            var repoPath = await _gitOps.EnsureBranchAsync(delegation.BranchName, cancellationToken);
                            var reconciledFiles = new List<string>();

                            foreach (var file in filesDoc.RootElement.EnumerateArray())
                            {
                                var filename = file.GetProperty("filename").GetString() ?? "";
                                var status = file.GetProperty("status").GetString() ?? "";

                                if (status == "removed" || string.IsNullOrEmpty(filename))
                                    continue;

                                // Fetch raw file content from Copilot's branch
                                var content = await FetchFileContentAsync(httpClient, filename, copilotBranch, cancellationToken);
                                if (content is not null)
                                {
                                    await _gitOps.WriteFileAsync(repoPath, filename, content, cancellationToken);
                                    reconciledFiles.Add(filename);
                                }
                            }

                            reconciledFileCount = reconciledFiles.Count;

                            // Update state.json with coding artifacts
                            await using var context = _contextFactory.Create(delegation.WorkItemId, repoPath);
                            var state = await context.LoadStateAsync(cancellationToken);

                            foreach (var file in reconciledFiles)
                                state.Artifacts.Code.Add(file);

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
                                ["filesChanged"] = reconciledFileCount,
                                ["linesAdded"] = linesAdded,
                                ["linesDeleted"] = linesDeleted,
                                ["durationMinutes"] = elapsed,
                                ["commitCount"] = prCommits,
                                ["reconciledByTimer"] = true
                            };
                            state.CurrentState = "AI Review"; // Skip Testing — Copilot already tested
                            state.Agents["Testing"] = AgentStatus.Skipped("Copilot coding agent already runs tests");
                            await context.SaveStateAsync(state, cancellationToken);

                            // Commit reconciled changes to pipeline branch
                            if (reconciledFileCount > 0)
                            {
                                await _gitOps.CommitAndPushAsync(repoPath,
                                    $"[AI Coding] US-{delegation.WorkItemId}: Copilot implementation (PR #{prNumber}, {reconciledFileCount} files)",
                                    cancellationToken);
                            }

                            // Close Copilot's PR — changes are on the pipeline branch now
                            try
                            {
                                var closeComment = JsonSerializer.Serialize(new
                                {
                                    body = $"Changes incorporated into pipeline branch `{delegation.BranchName}`. Pipeline continuing via ADO-Agent."
                                });
                                var closeCommentContent = new StringContent(closeComment, Encoding.UTF8, "application/json");
                                await httpClient.PostAsync(
                                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{prNumber}/comments",
                                    closeCommentContent, cancellationToken);

                                var closePrBody = JsonSerializer.Serialize(new { state = "closed" });
                                var closePrContent = new StringContent(closePrBody, Encoding.UTF8, "application/json");
                                await httpClient.PatchAsync(
                                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}",
                                    closePrContent, cancellationToken);

                                _logger.LogInformation("Closed Copilot PR #{PrNumber} after reconciliation", prNumber);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to close Copilot PR #{PrNumber} — may need manual cleanup", prNumber);
                            }

                            _logger.LogInformation(
                                "Reconciled {FileCount} files from Copilot PR #{PrNumber} onto branch {Branch} for WI-{WorkItemId}",
                                reconciledFileCount, prNumber, delegation.BranchName, delegation.WorkItemId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to reconcile files from Copilot PR #{PrNumber} for WI-{WorkItemId} — completing delegation without file reconciliation",
                    prNumber, delegation.WorkItemId);
            }
        }

        // 2. Mark delegation as completed
        delegation.Status = "Completed";
        delegation.CopilotPrNumber = prNumber;
        delegation.CompletedAt = DateTime.UtcNow;
        await _delegationService.UpdateAsync(delegation, cancellationToken);

        // 3. Update ADO work item state and fields
        try
        {
            await _adoClient.UpdateWorkItemFieldAsync(
                delegation.WorkItemId, CustomFieldNames.Paths.LastAgent, "Coding", cancellationToken);
        }
        catch { /* field may not exist yet */ }

        // Skip Testing — Copilot coding agent already runs tests during its session.
        // Go directly to AI Review state.
        await _adoClient.UpdateWorkItemStateAsync(
            delegation.WorkItemId, "AI Review", cancellationToken);

        // 4. Add completion comment to ADO
        var prInfo = prNumber > 0
            ? $"PR #{prNumber}, {prCommits} commits, {reconciledFileCount} files reconciled, +{linesAdded}/-{linesDeleted} lines"
            : "no PR found";
        await _adoClient.AddWorkItemCommentAsync(delegation.WorkItemId,
            $"<b>\U0001f916 AI Coding Agent Complete (GitHub Copilot)</b><br/>" +
            $"Strategy: Copilot coding agent<br/>" +
            $"{prInfo}<br/>" +
            $"Duration: {elapsed:F1} minutes (1 premium credit)<br/>" +
            $"Skipping Testing agent (Copilot already validated code)",
            cancellationToken);

        // 5. Enqueue Review agent — skip Testing since Copilot handles testing in its process
        var nextTask = new AgentTask
        {
            WorkItemId = delegation.WorkItemId,
            AgentType = AgentType.Review,
            CorrelationId = delegation.CorrelationId
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        await _activityLogger.LogAsync("Coding", delegation.WorkItemId,
            $"Copilot completed ({prInfo}, {elapsed:F1}m) — skipping Testing, enqueued Review agent (1 premium credit)",
            "info", cancellationToken);

        _logger.LogInformation(
            "Copilot delegation completed for WI-{WorkItemId} — PR #{PrNumber}, {FileCount} files reconciled, pipeline resumed (Testing skipped → Review)",
            delegation.WorkItemId, prNumber, reconciledFileCount);
    }

    /// <summary>
    /// Fetches raw file content from a specific branch via GitHub API.
    /// </summary>
    private async Task<string?> FetchFileContentAsync(HttpClient httpClient, string filePath, string branch, CancellationToken cancellationToken)
    {
        // Use a separate client or clear/re-add accept header for raw content
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/contents/{Uri.EscapeDataString(filePath)}?ref={branch}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch {File} from branch {Branch}: {Status}",
                filePath, branch, response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task HandleTimeoutAsync(CopilotDelegation delegation, CancellationToken cancellationToken)
    {
        var elapsed = (DateTime.UtcNow - delegation.DelegatedAt).TotalMinutes;

        // Safety guard: if the delegation is NOT actually past the timeout,
        // skip it. This protects against query bugs returning recent delegations.
        if (elapsed < _copilotOptions.TimeoutMinutes)
        {
            _logger.LogDebug(
                "Delegation for WI-{WorkItemId} only {Elapsed:F0}m old (timeout={Timeout}m) — skipping",
                delegation.WorkItemId, elapsed, _copilotOptions.TimeoutMinutes);
            return;
        }

        _logger.LogWarning(
            "Copilot delegation timed out for WI-{WorkItemId} (waited {Elapsed:F0}m, timeout={Timeout}m)",
            delegation.WorkItemId, elapsed, _copilotOptions.TimeoutMinutes);

        // Update delegation status
        delegation.Status = "TimedOut";
        delegation.CompletedAt = DateTime.UtcNow;
        await _delegationService.UpdateAsync(delegation, cancellationToken);

        // Close the GitHub Issue if one was created
        if (delegation.IssueNumber > 0)
        {
            try
            {
                await CloseIssueWithTimeoutAsync(delegation.IssueNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close GitHub Issue #{IssueNumber} for timeout", delegation.IssueNumber);
            }
        }

        // Persist forceAgentic flag in state.json so ResolveStrategy picks the agentic loop
        try
        {
            var repoPath = await _gitOps.EnsureBranchAsync(delegation.BranchName, cancellationToken);
            await using var context = _contextFactory.Create(delegation.WorkItemId, repoPath);
            var state = await context.LoadStateAsync(cancellationToken);

            state.Agents["Coding"] = AgentStatus.InProgress();
            state.Agents["Coding"].AdditionalData = new Dictionary<string, object>
            {
                ["forceAgentic"] = true,
                ["copilotTimedOut"] = true,
                ["copilotElapsedMinutes"] = elapsed
            };
            await context.SaveStateAsync(state, cancellationToken);

            await _gitOps.CommitAndPushAsync(repoPath,
                $"[AI Coding] US-{delegation.WorkItemId}: Copilot timed out — falling back to agentic loop",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist forceAgentic flag for WI-{WorkItemId}", delegation.WorkItemId);
        }

        // Re-enqueue for agentic coding — forceAgentic flag is now in state.json
        var task = new AgentTask
        {
            WorkItemId = delegation.WorkItemId,
            AgentType = AgentType.Coding,
            CorrelationId = delegation.CorrelationId
        };
        await _taskQueue.EnqueueAsync(task, cancellationToken);

        await _activityLogger.LogAsync("Coding", delegation.WorkItemId,
            $"Copilot timed out after {elapsed:F0}m — falling back to agentic coding loop",
            "warning", cancellationToken);
    }

    private async Task CloseIssueWithTimeoutAsync(int issueNumber, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _githubOptions.Token);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        // Comment explaining timeout
        var commentBody = JsonSerializer.Serialize(new
        {
            body = "Copilot coding agent timed out. Falling back to built-in agentic coding loop."
        });
        var commentContent = new StringContent(commentBody, Encoding.UTF8, "application/json");
        await httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/comments",
            commentContent, cancellationToken);

        // Close issue
        var closeBody = JsonSerializer.Serialize(new { state = "closed" });
        var closeContent = new StringContent(closeBody, Encoding.UTF8, "application/json");
        await httpClient.PatchAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}",
            closeContent, cancellationToken);

        _logger.LogInformation("Closed timed-out GitHub Issue #{IssueNumber}", issueNumber);
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
}
