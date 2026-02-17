using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Timer-triggered Azure Function that checks for timed-out Copilot delegations.
/// 
/// Runs every 5 minutes. If a Copilot delegation has been pending longer than
/// <see cref="CopilotOptions.TimeoutMinutes"/>, it re-enqueues the story for
/// coding with a "forceAgentic" flag so the built-in loop handles it instead.
/// 
/// This is a safety net — ensures no story gets stuck waiting for Copilot forever.
/// </summary>
public sealed class CopilotTimeoutChecker
{
    private readonly CopilotOptions _copilotOptions;
    private readonly GitHubOptions _githubOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly IAgentTaskQueue _taskQueue;
    private readonly IActivityLogger _activityLogger;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ILogger<CopilotTimeoutChecker> _logger;

    public CopilotTimeoutChecker(
        IOptions<CopilotOptions> copilotOptions,
        IOptions<GitHubOptions> githubOptions,
        ICopilotDelegationService delegationService,
        IAgentTaskQueue taskQueue,
        IActivityLogger activityLogger,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ILogger<CopilotTimeoutChecker> logger)
    {
        _copilotOptions = copilotOptions.Value;
        _githubOptions = githubOptions.Value;
        _delegationService = delegationService;
        _taskQueue = taskQueue;
        _activityLogger = activityLogger;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs every 5 minutes to check for completed or timed-out Copilot delegations.
    /// First checks if Copilot has finished (PR exists with commits) and auto-closes
    /// the issue to trigger the bridge webhook. If truly timed out, falls back to agentic.
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
    /// by finding a matching PR and auto-closing the issue to trigger the bridge webhook.
    /// </summary>
    private async Task CheckForCompletionAsync(CopilotDelegation delegation, CancellationToken cancellationToken)
    {
        var elapsed = (DateTime.UtcNow - delegation.DelegatedAt).TotalMinutes;

        // Don't check too early — give Copilot at least 3 minutes to start
        if (elapsed < 3)
        {
            return;
        }

        using var httpClient = CreateGitHubClient();

        // Check if the issue is already closed (webhook should handle it)
        var issueResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{delegation.IssueNumber}",
            cancellationToken);
        if (!issueResponse.IsSuccessStatusCode) return;

        var issueJson = await issueResponse.Content.ReadAsStringAsync(cancellationToken);
        using var issueDoc = JsonDocument.Parse(issueJson);
        var issueState = issueDoc.RootElement.GetProperty("state").GetString();

        if (issueState == "closed")
        {
            _logger.LogDebug("Issue #{IssueNumber} already closed for WI-{WorkItemId} — webhook should handle it",
                delegation.IssueNumber, delegation.WorkItemId);
            return;
        }

        // Look for a PR created by Copilot for this work item
        var prResponse = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls?state=all&sort=created&direction=desc&per_page=10",
            cancellationToken);
        if (!prResponse.IsSuccessStatusCode) return;

        var prJson = await prResponse.Content.ReadAsStringAsync(cancellationToken);
        using var prDoc = JsonDocument.Parse(prJson);

        foreach (var pr in prDoc.RootElement.EnumerateArray())
        {
            var prTitle = pr.GetProperty("title").GetString() ?? "";
            var prBody = pr.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";
            var prCreated = pr.GetProperty("created_at").GetDateTime();

            if (prCreated < delegation.DelegatedAt.AddMinutes(-1)) continue;

            // Check if this PR references our work item
            if (!prTitle.Contains($"US-{delegation.WorkItemId}", StringComparison.OrdinalIgnoreCase) &&
                !prBody.Contains($"US-{delegation.WorkItemId}", StringComparison.OrdinalIgnoreCase) &&
                !prTitle.Contains(delegation.WorkItemId.ToString()) &&
                !prBody.Contains($"#{delegation.IssueNumber}"))
            {
                continue;
            }

            var prNumber = pr.GetProperty("number").GetInt32();
            var prCommits = pr.GetProperty("commits").GetInt32();

            if (prCommits > 0)
            {
                _logger.LogInformation(
                    "Copilot completed! PR #{PrNumber} has {Commits} commits for WI-{WorkItemId} — auto-closing Issue #{IssueNumber}",
                    prNumber, prCommits, delegation.WorkItemId, delegation.IssueNumber);

                await _activityLogger.LogAsync("Coding", delegation.WorkItemId,
                    $"Detected Copilot completion (PR #{prNumber}, {prCommits} commits) — closing issue to trigger reconciliation",
                    "info", cancellationToken);

                // Close the issue — this fires the webhook which handles reconciliation
                var closeBody = JsonSerializer.Serialize(new { state = "closed" });
                var closeContent = new StringContent(closeBody, Encoding.UTF8, "application/json");
                await httpClient.PatchAsync(
                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{delegation.IssueNumber}",
                    closeContent, cancellationToken);

                return; // Webhook will handle the rest
            }
        }

        _logger.LogDebug("No completed PR found yet for WI-{WorkItemId} ({Elapsed:F0}m elapsed)",
            delegation.WorkItemId, elapsed);
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
