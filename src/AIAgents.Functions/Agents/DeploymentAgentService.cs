using System.Net;
using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Deployment agent: makes merge/deploy decisions based on autonomy level and review score.
/// Runs after DocumentationAgent creates the PR.
///
/// Autonomy Levels:
///   1-2: Never reached (dispatcher early-exits).
///   3 (Review &amp; Pause): Assigns a human reviewer, sets state to "Code Review".
///   4 (Auto-Merge): Merges the PR if review score meets threshold, sets "Ready for Deployment".
///   5 (Full Autonomy): Merges + triggers deployment pipeline, sets "Deployed".
/// </summary>
public sealed class DeploymentAgentService : IAgentService
{
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IRepositoryProvider _repoProvider;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly IActivityLogger _activityLogger;
    private readonly DeploymentOptions _options;
    private readonly ILogger<DeploymentAgentService> _logger;

    public DeploymentAgentService(
        IAzureDevOpsClient adoClient,
        IRepositoryProvider repoProvider,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        IActivityLogger activityLogger,
        IOptions<DeploymentOptions> options,
        ILogger<DeploymentAgentService> logger)
    {
        _adoClient = adoClient;
        _repoProvider = repoProvider;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _activityLogger = activityLogger;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        try
        {
        _logger.LogInformation("Deployment agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "AI Deployment";
        state.Agents["Deployment"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, AIPipelineNames.ProcessingState, cancellationToken);
        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Deployment, cancellationToken); }
        catch { /* field may not exist yet */ }

        var autonomyLevel = workItem.AutonomyLevel;
        var minimumScore = workItem.MinimumReviewScore;

        // Extract review score from previous agent's state
        var reviewScore = ExtractReviewScore(state);

        // Extract PR ID from documentation agent's state
        var prId = ExtractPullRequestId(state);

        _logger.LogInformation(
            "Deployment decision for WI-{WorkItemId}: autonomy={Level}, reviewScore={Score}, minScore={Min}, prId={PrId}",
            task.WorkItemId, autonomyLevel, reviewScore, minimumScore, prId);

        var decision = await MakeDeploymentDecisionAsync(
            workItem, autonomyLevel, reviewScore, minimumScore, prId, cancellationToken);

        // Update state
        state.Agents["Deployment"] = AgentStatus.Completed();
        state.Agents["Deployment"].AdditionalData = new Dictionary<string, object>
        {
            ["autonomyLevel"] = autonomyLevel,
            ["reviewScore"] = reviewScore ?? -1,
            ["decision"] = decision.Action,
            ["reason"] = decision.Reason
        };
        state.CurrentState = decision.FinalState;
        await context.SaveStateAsync(state, cancellationToken);

        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, string.Empty, cancellationToken); }
        catch { /* field may not exist yet */ }

        // Update ADO work item state
        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, decision.FinalState, cancellationToken);

        // Write all AI tracking fields to ADO custom fields (single batch API call)
        var tokenUsage = state.TokenUsage;
        try
        {
            var fieldUpdates = new Dictionary<string, object>
            {
                [CustomFieldNames.Paths.TokensUsed] = tokenUsage.TotalTokens,
                [CustomFieldNames.Paths.Cost] = $"${tokenUsage.TotalCost:F4}",
                [CustomFieldNames.Paths.Complexity] = tokenUsage.Complexity,
                [CustomFieldNames.Paths.LastAgent] = "Deployment",
                [CustomFieldNames.Paths.DeploymentDecision] = decision.Action
            };

            // Build per-agent model summary (e.g., "Planning: gpt-4o, Coding: claude-opus")
            var modelParts = tokenUsage.Agents
                .Where(kv => !string.IsNullOrEmpty(kv.Value.Model))
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}: {kv.Value.Model}")
                .ToList();
            if (modelParts.Count > 0)
                fieldUpdates[CustomFieldNames.Paths.Model] = string.Join(", ", modelParts);

            // Add review score if available
            if (reviewScore.HasValue)
                fieldUpdates[CustomFieldNames.Paths.ReviewScore] = reviewScore.Value;

            // Add critical issues count from review
            var criticalCount = ExtractCriticalIssueCount(state);
            if (criticalCount.HasValue)
                fieldUpdates[CustomFieldNames.Paths.CriticalIssues] = criticalCount.Value;

            // Add files generated count from coding agent
            var filesGenerated = ExtractFilesGenerated(state);
            if (filesGenerated.HasValue)
                fieldUpdates[CustomFieldNames.Paths.FilesGenerated] = filesGenerated.Value;

            // Add tests generated count from testing agent
            var testsGenerated = ExtractTestsGenerated(state);
            if (testsGenerated.HasValue)
                fieldUpdates[CustomFieldNames.Paths.TestsGenerated] = testsGenerated.Value;

            // Add PR number from documentation agent
            if (prId.HasValue)
                fieldUpdates[CustomFieldNames.Paths.PRNumber] = prId.Value;

            // Add processing time from state timestamps
            var processingTime = (state.UpdatedAt - state.CreatedAt).TotalSeconds;
            if (processingTime > 0)
                fieldUpdates[CustomFieldNames.Paths.ProcessingTime] = Math.Round((decimal)processingTime, 1);

            await _adoClient.UpdateWorkItemFieldsAsync(workItem.Id, fieldUpdates, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update AI custom fields on WI-{WorkItemId} — some fields may not exist yet",
                workItem.Id);
        }

        // Build token summary for comment
        var tokenSummary = BuildTokenSummary(tokenUsage);

        // Post summary comment
        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>\ud83e\udd16 AI Deployment Agent</b><br/>" +
            $"<b>Autonomy Level:</b> {autonomyLevel}<br/>" +
            $"<b>Review Score:</b> {reviewScore?.ToString() ?? "N/A"}<br/>" +
            $"<b>Decision:</b> {decision.Action}<br/>" +
            $"<b>Reason:</b> {decision.Reason}<br/>" +
            $"<b>Final State:</b> {decision.FinalState}<br/><br/>" +
            tokenSummary,
            cancellationToken);

        await _activityLogger.LogAsync(
            "Deployment", task.WorkItemId, decision.Action, cancellationToken: cancellationToken);

        // Log token usage summary as a separate activity entry for dashboard aggregation
        if (tokenUsage.TotalTokens > 0)
        {
            await _activityLogger.LogAsync(
                "TokenSummary", task.WorkItemId,
                $"Pipeline total: {tokenUsage.TotalTokens:N0} tokens, ${tokenUsage.TotalCost:F4}, complexity: {tokenUsage.Complexity}",
                tokenUsage.TotalTokens, tokenUsage.TotalCost,
                cancellationToken: cancellationToken);
        }

        _logger.LogInformation(
            "Deployment agent completed for WI-{WorkItemId}: {Action} → {State}",
            task.WorkItemId, decision.Action, decision.FinalState);

            // Deployment agent doesn't call AI itself, so report 0 tokens
            // (pipeline totals are in the TokenSummary activity entry)
            return AgentResult.Ok(0, 0m);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"Rate limit hit for Deployment agent on WI-{task.WorkItemId}", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AgentResult.Fail(ErrorCategory.Configuration, $"Authentication failed for Deployment agent on WI-{task.WorkItemId}. Check API key.", ex);
        }
        catch (HttpRequestException ex)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"HTTP error in Deployment agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(ErrorCategory.Code, $"Unexpected error in Deployment agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
    }

    private async Task<DeploymentDecision> MakeDeploymentDecisionAsync(
        StoryWorkItem workItem,
        int autonomyLevel,
        int? reviewScore,
        int minimumScore,
        int? prId,
        CancellationToken cancellationToken)
    {
        // Level 3: Pause for human review
        if (autonomyLevel <= 3)
        {
            return new DeploymentDecision(
                Action: "Assigned for human review",
                Reason: $"Autonomy level {autonomyLevel} requires human approval before merge.",
                FinalState: "Code Review");
        }

        // Levels 4-5: Check review score BEFORE any automated action
        if (reviewScore is null || reviewScore < minimumScore)
        {
            return new DeploymentDecision(
                Action: "Blocked — review score below threshold",
                Reason: $"Review score {reviewScore?.ToString() ?? "N/A"} is below minimum {minimumScore}. " +
                        "Requires human review before merge.",
                FinalState: "Needs Revision");
        }

        // Level 4: Auto-merge only
        if (autonomyLevel == 4)
        {
            if (prId is not null)
            {
                await _repoProvider.MergePullRequestAsync(prId.Value, cancellationToken);

                return new DeploymentDecision(
                    Action: $"Auto-merged PR #{prId}",
                    Reason: $"Review score {reviewScore} meets threshold {minimumScore}. PR merged via squash.",
                    FinalState: "Ready for Deployment");
            }

            return new DeploymentDecision(
                Action: "Skipped merge — no PR ID found",
                Reason: "DocumentationAgent did not record a PR ID. Manual merge required.",
                FinalState: "Ready for Deployment");
        }

        // Level 5: Auto-merge + trigger deployment
        if (autonomyLevel >= 5)
        {
            if (prId is not null)
            {
                await _repoProvider.MergePullRequestAsync(prId.Value, cancellationToken);
            }

            try
            {
                var runId = await _repoProvider.TriggerDeploymentAsync("main", cancellationToken);

                return new DeploymentDecision(
                    Action: $"Auto-merged PR #{prId} and triggered deployment run #{runId}",
                    Reason: $"Review score {reviewScore} meets threshold {minimumScore}. " +
                            $"Full autonomy: merged + deployed via '{_options.PipelineName}'.",
                    FinalState: "Deployed");
            }
            catch (InvalidOperationException ex)
            {
                // No pipeline/workflow configured
                _logger.LogWarning(ex, "Deployment trigger not configured");

                return new DeploymentDecision(
                    Action: $"Auto-merged PR #{prId} — no deployment configured",
                    Reason: $"Review score {reviewScore} meets threshold {minimumScore}. " +
                            "Merged, but no deployment pipeline/workflow configured.",
                    FinalState: "Ready for Deployment");
            }
        }

        // Should not reach here
        return new DeploymentDecision(
            Action: "No action taken",
            Reason: $"Unknown autonomy level {autonomyLevel}",
            FinalState: "Ready for QA");
    }

    private static int? ExtractReviewScore(StoryState state)
    {
        if (state.Agents.TryGetValue("Review", out var reviewStatus)
            && reviewStatus.AdditionalData?.TryGetValue("score", out var scoreObj) == true)
        {
            return scoreObj switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                System.Text.Json.JsonElement je when je.TryGetInt32(out var jInt) => jInt,
                _ => null
            };
        }

        return null;
    }

    private static int? ExtractCriticalIssueCount(StoryState state)
    {
        if (state.Agents.TryGetValue("Review", out var reviewStatus)
            && reviewStatus.AdditionalData?.TryGetValue("criticalIssues", out var obj) == true)
        {
            return obj switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                System.Text.Json.JsonElement je when je.TryGetInt32(out var jInt) => jInt,
                _ => null
            };
        }

        return null;
    }

    private static int? ExtractFilesGenerated(StoryState state)
    {
        if (state.Agents.TryGetValue("Coding", out var codingStatus)
            && codingStatus.AdditionalData?.TryGetValue("filesGenerated", out var obj) == true)
        {
            return obj switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                System.Text.Json.JsonElement je when je.TryGetInt32(out var jInt) => jInt,
                _ => null
            };
        }

        return null;
    }

    private static int? ExtractTestsGenerated(StoryState state)
    {
        if (state.Agents.TryGetValue("Testing", out var testingStatus)
            && testingStatus.AdditionalData?.TryGetValue("testsGenerated", out var obj) == true)
        {
            return obj switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                System.Text.Json.JsonElement je when je.TryGetInt32(out var jInt) => jInt,
                _ => null
            };
        }

        return null;
    }

    private static int? ExtractPullRequestId(StoryState state)
    {
        if (state.Agents.TryGetValue("Documentation", out var docStatus)
            && docStatus.AdditionalData?.TryGetValue("prId", out var prIdObj) == true)
        {
            return prIdObj switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                System.Text.Json.JsonElement je when je.TryGetInt32(out var jInt) => jInt,
                _ => null
            };
        }

        return null;
    }

    private sealed record DeploymentDecision(string Action, string Reason, string FinalState);

    private static string BuildTokenSummary(StoryTokenUsage usage)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<b>📊 AI Token Usage Summary</b><br/>");
        sb.AppendLine($"<b>Total Tokens:</b> {usage.TotalTokens:N0} (in: {usage.TotalInputTokens:N0} / out: {usage.TotalOutputTokens:N0})<br/>");
        sb.AppendLine($"<b>Estimated Cost:</b> ${usage.TotalCost:F4}<br/>");
        sb.AppendLine($"<b>Complexity:</b> {usage.Complexity}<br/><br/>");

        if (usage.Agents.Count > 0)
        {
            sb.AppendLine("<b>Per-Agent Breakdown:</b><br/>");
            sb.AppendLine("<table><tr><th>Agent</th><th>Tokens</th><th>Cost</th><th>Model</th><th>Calls</th></tr>");
            foreach (var (agent, data) in usage.Agents.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"<tr><td>{agent}</td><td>{data.TotalTokens:N0}</td>" +
                    $"<td>${data.EstimatedCost:F4}</td><td>{data.Model}</td><td>{data.CallCount}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        return sb.ToString();
    }
}
