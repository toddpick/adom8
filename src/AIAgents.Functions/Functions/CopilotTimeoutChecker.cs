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
/// Timer-triggered Azure Function that acts as a safety-net for Copilot delegations.
/// 
/// Runs every 2 minutes. Checks if any pending Copilot delegation has exceeded
/// <see cref="CopilotOptions.TimeoutMinutes"/> (default 30 minutes) and falls back
/// to the built-in agentic coding loop if so.
/// 
/// Primary completion detection is handled by the CopilotBridgeWebhook, which uses
/// PR-readiness heuristics (draft==false, no [WIP], reviewer requested) to detect
/// when Copilot has finished. This timer only fires if the webhook never triggers
/// (e.g., Copilot leaves the PR as draft indefinitely or never creates one).
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
    /// Runs every 2 minutes. Only checks for timed-out delegations and falls back
    /// to the agentic coding loop. Completion detection is handled by the webhook.
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
}
