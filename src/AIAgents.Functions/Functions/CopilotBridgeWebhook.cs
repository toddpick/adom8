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
/// Uses PR-update heuristics to detect when Copilot has finished coding:
/// 1. Validates the GitHub webhook signature (X-Hub-Signature-256)
/// 2. Listens for pull_request update events (edited/review_requested/synchronize/ready_for_review)
/// 3. Waits for non-[WIP] title and at least one changed file before treating PR as complete
/// 4. Matches the PR to a pending Copilot delegation by US-{id} in title/body or branch name
/// 5. Reconciles files from Copilot's PR branch onto the pipeline branch
/// 6. Resumes the pipeline by enqueuing the Review agent
/// 
/// The CopilotTimeoutChecker timer serves as a safety-net fallback if the webhook
/// is not triggered (e.g., Copilot leaves the PR as draft indefinitely).
/// </summary>
public sealed class CopilotBridgeWebhook
{
    private const string CheckpointLastAgent = "LastAgent";
    private const string CheckpointCurrentAiAgent = "CurrentAIAgent";
    private const string CheckpointCompletionComment = "CompletionComment";

    private readonly CopilotOptions _copilotOptions;
    private readonly GitHubOptions _githubOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly IGitHubTokenResolver _gitHubTokenResolver;
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
        IGitHubTokenResolver? gitHubTokenResolver,
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
        _gitHubTokenResolver = gitHubTokenResolver ??
            new DefaultGitHubTokenResolver(_githubOptions.Token);
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _adoClient = adoClient;
        _taskQueue = taskQueue;
        _activityLogger = activityLogger;
        _logger = logger;
    }

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
        : this(
            copilotOptions,
            githubOptions,
            delegationService,
            null,
            gitOps,
            contextFactory,
            adoClient,
            taskQueue,
            activityLogger,
            logger)
    {
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

        if (!req.Headers.TryGetValues("X-GitHub-Event", out var eventTypes))
        {
            _logger.LogDebug("No X-GitHub-Event header");
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var eventType = eventTypes.First();

        // Only process pull_request events — issue-based detection was unreliable
        // because cancelled/failed Copilot sessions also close the issue.
        if (eventType == "pull_request")
        {
            using var doc = JsonDocument.Parse(body);
            return await HandlePullRequestEventAsync(req, doc.RootElement, cancellationToken);
        }

        _logger.LogDebug("Ignoring {EventType} event — only pull_request events are processed", eventType);
        return req.CreateResponse(HttpStatusCode.OK);
    }

    /// <summary>
    /// Handles pull_request webhook events using update heuristics.
    /// 
    /// We intentionally do not proceed on PR creation (`opened`) to avoid premature
    /// advancement before Copilot has produced meaningful updates.
    /// </summary>
    private async Task<HttpResponseData> HandlePullRequestEventAsync(
        HttpRequestData req, JsonElement root, CancellationToken cancellationToken)
    {
        var action = root.GetProperty("action").GetString() ?? "";

        // Only react to PR update actions, not initial creation.
        if (action is not ("ready_for_review" or "edited" or "review_requested" or "synchronize"))
        {
            _logger.LogDebug("Ignoring pull_request action: {Action}", action);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var pr = root.GetProperty("pull_request");
        var prNumber = pr.GetProperty("number").GetInt32();
        var prTitle = pr.GetProperty("title").GetString() ?? "";
        var prBody = pr.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        var copilotBranch = pr.GetProperty("head").GetProperty("ref").GetString() ?? "";
        var baseBranch = pr.TryGetProperty("base", out var baseProp)
            ? baseProp.TryGetProperty("ref", out var baseRef) ? baseRef.GetString() ?? "" : ""
            : "";

        // ── Update gating ──
        var notWip = !prTitle.Contains("[WIP]", StringComparison.OrdinalIgnoreCase);
        var isReady = IsReadyToReconcile(action, prTitle);

        _logger.LogInformation(
            "PR #{PrNumber} action={Action}: wip={HasWip}, ready={IsReady}",
            prNumber, action, !notWip, isReady);

        if (!isReady)
        {
            _logger.LogInformation(
                "PR #{PrNumber} is not ready — waiting for update action and non-[WIP] title",
                prNumber);
            var waitResponse = req.CreateResponse(HttpStatusCode.OK);
            await waitResponse.WriteStringAsync(
                $"PR #{prNumber} not ready. Waiting.", cancellationToken);
            return waitResponse;
        }

        // ── Match PR to a pending Copilot delegation ──
        var workItemId = ExtractWorkItemId(prTitle, prBody);
        CopilotDelegation? delegation = null;

        if (workItemId is not null)
        {
            delegation = await _delegationService.GetByWorkItemIdAsync(workItemId.Value, cancellationToken);
        }

        // Fallback: match by base branch name (Copilot PRs target the pipeline branch)
        if (delegation is null || delegation.Status != "Pending")
        {
            if (!string.IsNullOrEmpty(baseBranch))
            {
                var pending = await _delegationService.GetPendingAsync(cancellationToken);
                delegation = pending.FirstOrDefault(d =>
                    string.Equals(d.BranchName, baseBranch, StringComparison.OrdinalIgnoreCase));

                if (delegation is not null)
                {
                    workItemId = delegation.WorkItemId;
                    _logger.LogInformation(
                        "Matched PR #{PrNumber} to WI-{WorkItemId} via base branch {Branch}",
                        prNumber, workItemId, baseBranch);
                }
            }
        }

        if (delegation is null || delegation.Status != "Pending")
        {
            _logger.LogDebug(
                "PR #{PrNumber} does not match any pending Copilot delegation (workItemId={WorkItemId})",
                prNumber, workItemId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        _logger.LogInformation(
            "Copilot PR #{PrNumber} ready for WI-{WorkItemId} (action={Action}, wip={HasWip}) — reconciling",
            prNumber, delegation.WorkItemId, action, !notWip);

        try
        {
            try
            {
                await _activityLogger.LogAsync("Coding", delegation.WorkItemId,
                    $"Copilot PR #{prNumber} ready (action={action}, wip={!notWip}) — reconciling",
                    "info", cancellationToken);
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx,
                    "Non-critical activity log failed before reconciliation for PR #{PrNumber} WI-{WorkItemId}",
                    prNumber, delegation.WorkItemId);
            }

            await ReconcileAndResumeAsync(delegation, prNumber, copilotBranch, CancellationToken.None);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(
                $"Reconciled Copilot PR #{prNumber} for WI-{delegation.WorkItemId}", cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            var errorDetails = FormatExceptionDetails(ex);
            _logger.LogError(ex,
                "Failed to reconcile Copilot PR #{PrNumber} for WI-{WorkItemId}. Details: {ErrorDetails}",
                prNumber, delegation.WorkItemId, errorDetails);
            try
            {
                await _activityLogger.LogAsync("Coding", delegation.WorkItemId,
                    $"Error reconciling Copilot PR #{prNumber}: {errorDetails}", "error", CancellationToken.None);
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx,
                    "Non-critical activity log failed while reporting reconciliation error for PR #{PrNumber} WI-{WorkItemId}",
                    prNumber, delegation.WorkItemId);
            }
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
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
        var workItemId = delegation.WorkItemId;

        try
        {
            await _activityLogger.LogAsync("Coding", workItemId,
                $"Copilot PR #{prNumber} received — reconciling changes", "info", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Non-critical activity log failed for PR #{PrNumber} WI-{WorkItemId}",
                prNumber, workItemId);
        }

        // Fetch story metadata first to resolve per-story GitHub token selection
        var tokenSelection = _gitHubTokenResolver.ResolveDefault();
        var isInitializeCodebaseStory = false;
        try
        {
            var workItem = await _adoClient.GetWorkItemAsync(workItemId, cancellationToken);
            tokenSelection = _gitHubTokenResolver.ResolveForStory(workItem);
            isInitializeCodebaseStory = workItem.Tags.Any(tag =>
                string.Equals(tag, AIPipelineNames.InitializeCodebaseTag, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not evaluate InitializeCodebase tag for WI-{WorkItemId}; proceeding with standard Copilot handoff",
                workItemId);
        }

        _logger.LogInformation(
            "Using GitHub token alias '{Alias}' for Copilot reconciliation WI-{WorkItemId}",
            tokenSelection.Alias,
            workItemId);

        // Fetch PR file list and metrics from GitHub API
        var (changedFiles, metrics) = await FetchPrDetailsAsync(prNumber, delegation.DelegatedAt, tokenSelection.Token, cancellationToken);

        // Safety check: don't reconcile if Copilot hasn't pushed any actual file changes yet.
        // This prevents closing the issue and resuming the pipeline with 0 code changes.
        if (changedFiles.Count == 0)
        {
            _logger.LogInformation(
                "PR #{PrNumber} for WI-{WorkItemId} has no changed files yet — skipping reconciliation",
                prNumber, workItemId);
            try
            {
                await _activityLogger.LogAsync("Coding", workItemId,
                    $"Copilot PR #{prNumber} has no file changes yet — waiting for code", "info", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Non-critical activity log failed for empty-change PR #{PrNumber} WI-{WorkItemId}",
                    prNumber, workItemId);
            }
            return;
        }

        var reconciledFiles = changedFiles
            .Where(file => file.Status != "removed")
            .Select(file => file.Filename)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Close Copilot's PR if configured
        // Intentionally do not merge or close PR here. Human review controls merge decisions.

        // Close the GitHub Issue if one was created (safe if already closed)
        if (delegation.IssueNumber > 0)
        {
            try { await CloseIssueAsync(delegation.IssueNumber, tokenSelection.Token, cancellationToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to close issue #{IssueNumber} — may already be closed", delegation.IssueNumber); }
        }

        // Update ADO work item
        var lastAgentUpdated = false;
        try
        {
            await _adoClient.UpdateWorkItemFieldAsync(workItemId, CustomFieldNames.Paths.LastAgent, "Coding", cancellationToken);
            lastAgentUpdated = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update Last Agent for WI-{WorkItemId} during Copilot reconciliation",
                workItemId);
        }

        // Copilot path: skip Testing and move directly to Review (or complete initialize stories without review enqueue).
        var currentAgentUpdated = false;
        try
        {
            var nextAgentValue = isInitializeCodebaseStory
                ? string.Empty
                : AIPipelineNames.CurrentAgentValues.Review;

            await _adoClient.UpdateWorkItemFieldAsync(workItemId, CustomFieldNames.Paths.CurrentAIAgent, nextAgentValue, cancellationToken);
            currentAgentUpdated = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update Current AI Agent for WI-{WorkItemId} during Copilot reconciliation",
                workItemId);
        }

        try
        {
            await _activityLogger.LogAsync("Testing", workItemId,
                "Testing skipped — GitHub Copilot coding session already validated changes",
                "info", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Non-critical activity log failed for Testing skip note on WI-{WorkItemId}",
                workItemId);
        }

        var completionCommentAdded = false;
        try
        {
            await _adoClient.AddWorkItemCommentAsync(workItemId,
                $"<b>🤖 AI Coding Agent Complete (GitHub Copilot)</b><br/>" +
                $"Strategy: Copilot coding agent<br/>" +
                $"PR: #{prNumber} | Files: {metrics.FilesChanged} | +{metrics.LinesAdded}/-{metrics.LinesDeleted} lines<br/>" +
                $"Duration: {metrics.DurationMinutes:F1} minutes | Commits: {metrics.CommitCount}<br/>" +
                (isInitializeCodebaseStory
                    ? "Testing skipped (validated by Copilot session) → initialize flow complete (Review enqueue skipped for no-clone path)"
                    : "Testing skipped (validated by Copilot session) → handing off to Review agent"),
                cancellationToken);
            completionCommentAdded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Non-critical ADO comment failed for WI-{WorkItemId} PR #{PrNumber}",
                workItemId, prNumber);
        }

        if (_copilotOptions.CheckpointEnforcementEnabled)
        {
            var required = ParseRequiredAdoCheckpoints(_copilotOptions.RequiredAdoCheckpoints);
            var (passed, missing) = EvaluateRequiredCheckpointStatus(required, lastAgentUpdated, currentAgentUpdated, completionCommentAdded);
            if (!passed)
            {
                var missingLabel = string.Join(", ", missing);
                var failMessage = $"Checkpoint enforcement blocked Copilot handoff for US-{workItemId}. Missing required updates: {missingLabel}.";

                delegation.Status = _copilotOptions.CheckpointFailHard ? "Failed" : "Pending";
                delegation.CopilotPrNumber = prNumber;
                delegation.CompletedAt = _copilotOptions.CheckpointFailHard ? DateTime.UtcNow : null;
                await _delegationService.UpdateAsync(delegation, cancellationToken);

                try
                {
                    await _adoClient.AddWorkItemCommentAsync(workItemId,
                        $"⚠️ <b>Copilot completion checkpoint enforcement blocked handoff</b><br/>" +
                        $"PR: #{prNumber}<br/>" +
                        $"Missing required updates: {missingLabel}<br/>" +
                        $"Pipeline did not enqueue Review.",
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to post checkpoint-enforcement comment for WI-{WorkItemId}",
                        workItemId);
                }

                await _activityLogger.LogAsync(
                    "Coding",
                    workItemId,
                    failMessage,
                    "error",
                    cancellationToken);

                _logger.LogError(
                    "Copilot checkpoint enforcement failed for WI-{WorkItemId}. Missing: {Missing}",
                    workItemId,
                    missingLabel);

                if (_copilotOptions.CheckpointFailHard)
                {
                    throw new InvalidOperationException(failMessage);
                }

                return;
            }
        }

        // Log 1 token as a marker for "1 premium credit" — Copilot agent sessions
        // cost 1 GitHub premium request regardless of output size.
        var tokensForLog = 1;
        var costForLog = 0m; // No direct API cost — included in GitHub subscription

        if (isInitializeCodebaseStory)
        {
            try
            {
                await _adoClient.UpdateWorkItemStateAsync(workItemId, "Code Review", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to move InitializeCodebase story WI-{WorkItemId} to Code Review after Copilot reconciliation",
                    workItemId);
            }

            try
            {
                await _activityLogger.LogAsync("Coding", workItemId,
                    $"Copilot completed initialize flow for PR #{prNumber}. Review enqueue skipped to preserve no-clone path.",
                    tokensForLog, costForLog, "info", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Non-critical activity log failed for initialize no-clone completion on WI-{WorkItemId}",
                    workItemId);
            }

            delegation.Status = "Completed";
            delegation.CopilotPrNumber = prNumber;
            delegation.CompletedAt = DateTime.UtcNow;
            await _delegationService.UpdateAsync(delegation, cancellationToken);

            _logger.LogInformation(
                "Copilot bridge reconciled PR #{PrNumber} for initialize story WI-{WorkItemId} — review enqueue skipped (no-clone path preserved)",
                prNumber,
                workItemId);
            return;
        }

        // Enqueue Review agent
        var nextTask = new AgentTask
        {
            WorkItemId = workItemId,
            AgentType = AgentType.Review,
            CorrelationId = delegation.CorrelationId,
            TriggerSource = nameof(CopilotBridgeWebhook),
            ResumeFromStage = "Review",
            HandoffNote = $"Copilot reconciliation complete for PR #{prNumber}"
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        delegation.Status = "Completed";
        delegation.CopilotPrNumber = prNumber;
        delegation.CompletedAt = DateTime.UtcNow;
        await _delegationService.UpdateAsync(delegation, cancellationToken);

        try
        {
            await _activityLogger.LogAsync("Coding", workItemId,
                $"Copilot coding agent completed successfully — {metrics.FilesChanged} files, +{metrics.LinesAdded}/-{metrics.LinesDeleted} lines, {metrics.DurationMinutes:F1}m, {metrics.CommitCount} commits. Testing skipped → Review. (1 premium credit)",
                tokensForLog, costForLog, "info", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Non-critical activity log failed for reconciliation success summary on WI-{WorkItemId}",
                workItemId);
        }

        _logger.LogInformation(
            "Copilot bridge reconciled PR #{PrNumber} for WI-{WorkItemId} — {Files} files, pipeline resumed (Testing skipped → Review)",
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

    internal static bool IsReadyToReconcile(string action, string prTitle)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        if (string.Equals(action, "opened", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !prTitle.Contains("[WIP]", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> ParseRequiredAdoCheckpoints(string? configured)
    {
        var defaults = new[]
        {
            CheckpointLastAgent,
            CheckpointCurrentAiAgent,
            CheckpointCompletionComment
        };

        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaults;
        }

        var mapped = configured
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Select(MapCheckpointToken)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return mapped.Count == 0 ? defaults : mapped;
    }

    internal static (bool Passed, List<string> Missing) EvaluateRequiredCheckpointStatus(
        IReadOnlyCollection<string> required,
        bool lastAgentUpdated,
        bool currentAgentUpdated,
        bool completionCommentAdded)
    {
        var missing = new List<string>();

        foreach (var checkpoint in required)
        {
            if (checkpoint.Equals(CheckpointLastAgent, StringComparison.OrdinalIgnoreCase) && !lastAgentUpdated)
            {
                missing.Add(CheckpointLastAgent);
            }
            else if (checkpoint.Equals(CheckpointCurrentAiAgent, StringComparison.OrdinalIgnoreCase) && !currentAgentUpdated)
            {
                missing.Add(CheckpointCurrentAiAgent);
            }
            else if (checkpoint.Equals(CheckpointCompletionComment, StringComparison.OrdinalIgnoreCase) && !completionCommentAdded)
            {
                missing.Add(CheckpointCompletionComment);
            }
        }

        return (missing.Count == 0, missing);
    }

    private static string? MapCheckpointToken(string value) => value.ToLowerInvariant() switch
    {
        "lastagent" or "last_agent" or "last-agent" => CheckpointLastAgent,
        "currentaiagent" or "current_ai_agent" or "current-agent" => CheckpointCurrentAiAgent,
        "completioncomment" or "comment" or "completion_comment" => CheckpointCompletionComment,
        _ => null
    };

    private static string FormatExceptionDetails(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? "No exception message provided"
            : ex.Message.Trim();

        if (ex.InnerException is null)
        {
            return $"{ex.GetType().Name}: {message}";
        }

        var innerMessage = string.IsNullOrWhiteSpace(ex.InnerException.Message)
            ? "No inner exception message provided"
            : ex.InnerException.Message.Trim();

        return $"{ex.GetType().Name}: {message} | Inner {ex.InnerException.GetType().Name}: {innerMessage}";
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
        int prNumber,
        DateTime delegatedAt,
        string githubToken,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient(githubToken);

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
                Status = file.GetProperty("status").GetString() ?? "",
                ContentsUrl = file.TryGetProperty("contents_url", out var contentsUrl)
                    ? contentsUrl.GetString()
                    : null
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
        PrFile file,
        string branch,
        string githubToken,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient(githubToken);
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        // Preferred path: use GitHub PR file `contents_url` (commit-pinned).
        // This avoids branch/ref resolution failures with Copilot temporary branch naming.
        if (!string.IsNullOrWhiteSpace(file.ContentsUrl))
        {
            var contentsResponse = await httpClient.GetAsync(file.ContentsUrl, cancellationToken);
            if (contentsResponse.IsSuccessStatusCode)
            {
                return await contentsResponse.Content.ReadAsStringAsync(cancellationToken);
            }

            _logger.LogWarning(
                "Failed to fetch {File} via contents_url: {Status}",
                file.Filename,
                contentsResponse.StatusCode);
        }

        var encodedPath = Uri.EscapeDataString(file.Filename);
        var encodedBranch = Uri.EscapeDataString(branch);
        var response = await httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/contents/{encodedPath}?ref={encodedBranch}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch {File} from branch {Branch}: {Status}",
                file.Filename, branch, response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Closes a PR with a comment.
    /// </summary>
    private async Task ClosePullRequestAsync(int prNumber, string comment, string githubToken, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient(githubToken);

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
    private async Task CloseIssueAsync(int issueNumber, string githubToken, CancellationToken cancellationToken)
    {
        using var httpClient = CreateGitHubClient(githubToken);

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

    private HttpClient CreateGitHubClient(string githubToken)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", githubToken);
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
        public string? ContentsUrl { get; init; }
    }

    private sealed class DefaultGitHubTokenResolver : IGitHubTokenResolver
    {
        private readonly string _token;

        public DefaultGitHubTokenResolver(string token)
        {
            _token = token;
        }

        public GitHubTokenSelection ResolveForStory(StoryWorkItem workItem)
            => new("default", _token);

        public GitHubTokenSelection ResolveDefault()
            => new("default", _token);
    }
}
