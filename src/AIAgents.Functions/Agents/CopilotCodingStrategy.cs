using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Copilot coding strategy: delegates the coding work to GitHub Copilot's coding agent
/// by creating an ephemeral GitHub Issue assigned to <c>@copilot</c>.
/// 
/// The pipeline pauses after delegation. A webhook bridge (<see cref="Functions.CopilotBridgeWebhook"/>)
/// catches Copilot's PR, reconciles changes onto the pipeline branch, and resumes the pipeline.
/// 
/// This strategy does NOT enqueue the next agent — the bridge handles pipeline resumption.
/// </summary>
public sealed class CopilotCodingStrategy : ICodingStrategy
{
    private readonly GitHubOptions _githubOptions;
    private readonly CopilotOptions _copilotOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    private readonly string _agentAssignee;

    /// <summary>The GitHub agent username this strategy will assign issues to.</summary>
    public string AgentAssignee => _agentAssignee;

    public CopilotCodingStrategy(
        IOptions<GitHubOptions> githubOptions,
        IOptions<CopilotOptions> copilotOptions,
        ICopilotDelegationService delegationService,
        ILogger logger,
        string? agentOverride = null)
    {
        _githubOptions = githubOptions.Value;
        _copilotOptions = copilotOptions.Value;
        _delegationService = delegationService;
        _logger = logger;
        _agentAssignee = agentOverride ?? _copilotOptions.Model ?? "copilot";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _githubOptions.Token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<CodingResult> ExecuteAsync(CodingContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Delegating coding for WI-{WorkItemId} to GitHub Copilot coding agent",
            context.WorkItemId);

        // Guard against duplicate triggers — if a delegation already exists for this work item,
        // return immediately instead of creating another GitHub Issue + Copilot agent run.
        var existingDelegation = await _delegationService.GetByWorkItemIdAsync(context.WorkItemId, cancellationToken);
        if (existingDelegation is not null && existingDelegation.Status == "Pending")
        {
            _logger.LogWarning(
                "Duplicate Copilot delegation detected for WI-{WorkItemId} (Issue #{IssueNumber}) — skipping",
                context.WorkItemId, existingDelegation.IssueNumber);

            return new CodingResult
            {
                Success = true,
                Mode = "copilot-delegated",
                ModifiedFiles = [],
                Tokens = 0,
                Cost = 0m,
                Summary = $"Already delegated to Copilot (Issue #{existingDelegation.IssueNumber}). Pipeline waiting."
            };
        }

        await EnsureBranchExistsAsync(context.BranchName, cancellationToken);

        int issueNumber = 0;

        if (_copilotOptions.CreateIssue)
        {
            issueNumber = await CreateGitHubIssueAsync(context, cancellationToken);

            // Assign to Copilot coding agent via the proper GitHub API
            await AssignIssueToAgentAsync(issueNumber, context.BranchName, cancellationToken);
            await PostKickoffCommentAsync(issueNumber, context, cancellationToken);

            _logger.LogInformation(
                "Created GitHub Issue #{IssueNumber}, assigned Copilot agent, and posted kickoff for WI-{WorkItemId}",
                issueNumber, context.WorkItemId);
        }
        else
        {
            _logger.LogWarning(
                "Copilot:CreateIssue is false — pipeline paused for WI-{WorkItemId}. " +
                "Manual Copilot trigger required.",
                context.WorkItemId);
        }

        // Record delegation in table storage
        var delegation = new CopilotDelegation
        {
            WorkItemId = context.WorkItemId,
            IssueNumber = issueNumber,
            CorrelationId = context.CorrelationId,
            BranchName = context.BranchName
        };
        await _delegationService.CreateAsync(delegation, cancellationToken);

        // Update state to reflect delegation
        context.State.Agents["Coding"] = AgentStatus.InProgress();
        context.State.Agents["Coding"].AdditionalData = new Dictionary<string, object>
        {
            ["mode"] = "copilot-delegated",
            ["issueNumber"] = issueNumber,
            ["delegatedAt"] = DateTime.UtcNow.ToString("O"),
            ["agent"] = _agentAssignee
        };

        var summary = _copilotOptions.CreateIssue
            ? $"Delegated to GitHub Copilot (Issue #{issueNumber}). Pipeline paused — waiting for Copilot PR."
            : "Copilot delegation requested (CreateIssue=false). Manual action required.";

        return new CodingResult
        {
            Success = true,
            Mode = "copilot-delegated",
            ModifiedFiles = [],
            Tokens = 0,
            Cost = 0m,
            Summary = summary,
            CopilotMetrics = new CopilotMetrics
            {
                IssueNumber = issueNumber
            }
        };
    }

    private async Task EnsureBranchExistsAsync(string branchName, CancellationToken cancellationToken)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var refResponse = await _httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/git/ref/heads/{encodedBranch}",
            cancellationToken);

        if (refResponse.IsSuccessStatusCode)
        {
            return;
        }

        if (refResponse.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await refResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Could not verify branch {Branch} existence (status {StatusCode}): {Body}",
                branchName,
                refResponse.StatusCode,
                body);
            return;
        }

        var repoResponse = await _httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}",
            cancellationToken);
        repoResponse.EnsureSuccessStatusCode();

        var repoJson = await repoResponse.Content.ReadAsStringAsync(cancellationToken);
        using var repoDoc = JsonDocument.Parse(repoJson);
        var repositoryDefaultBranch = repoDoc.RootElement.GetProperty("default_branch").GetString() ?? "main";
        var preferredBaseBranch = string.IsNullOrWhiteSpace(_githubOptions.BaseBranch)
            ? repositoryDefaultBranch
            : _githubOptions.BaseBranch;

        var baseBranchToUse = preferredBaseBranch;

        var preferredRefResponse = await _httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/git/ref/heads/{Uri.EscapeDataString(preferredBaseBranch)}",
            cancellationToken);

        if (!preferredRefResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Configured GitHub base branch '{PreferredBranch}' not found or inaccessible (status {StatusCode}); falling back to repository default branch '{DefaultBranch}'",
                preferredBaseBranch,
                preferredRefResponse.StatusCode,
                repositoryDefaultBranch);
            baseBranchToUse = repositoryDefaultBranch;
        }

        var baseRefResponse = await _httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/git/ref/heads/{Uri.EscapeDataString(baseBranchToUse)}",
            cancellationToken);
        baseRefResponse.EnsureSuccessStatusCode();

        var baseRefJson = await baseRefResponse.Content.ReadAsStringAsync(cancellationToken);
        using var baseRefDoc = JsonDocument.Parse(baseRefJson);
        var sha = baseRefDoc.RootElement.GetProperty("object").GetProperty("sha").GetString();

        if (string.IsNullOrWhiteSpace(sha))
        {
            throw new InvalidOperationException($"Could not resolve base SHA for base branch '{baseBranchToUse}'.");
        }

        var createPayload = new
        {
            @ref = $"refs/heads/{branchName}",
            sha
        };

        var createResponse = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/git/refs",
            new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json"),
            cancellationToken);

        if (createResponse.IsSuccessStatusCode || createResponse.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // 422 means the branch was created concurrently.
            _logger.LogInformation(
                "Ensured branch {Branch} exists for Copilot delegation (base: {BaseBranch})",
                branchName,
                baseBranchToUse);
            return;
        }

        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Failed to create branch '{branchName}' for Copilot delegation (status {(int)createResponse.StatusCode}): {createBody}");
    }

    /// <summary>
    /// Creates an ephemeral GitHub Issue with the story plan and coding instructions.
    /// This issue serves as the "prompt" to Copilot and is auto-closed after reconciliation.
    /// </summary>
    private async Task<int> CreateGitHubIssueAsync(CodingContext context, CancellationToken cancellationToken)
    {
        var issueBody = BuildIssueBody(context, _agentAssignee);

        var requestBody = new
        {
            title = $"[US-{context.WorkItemId}] {context.WorkItem.Title}",
            body = issueBody,
            labels = new[] { "copilot-agent" }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues",
            content, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "GitHub Issue creation failed ({StatusCode}): {Body}",
                response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        using var doc = JsonDocument.Parse(responseBody);
        var number = doc.RootElement.GetProperty("number").GetInt32();

        return number;
    }

    /// <summary>
    /// Assigns the issue to the Copilot coding agent via the GitHub REST API.
    /// Uses <c>copilot-swe-agent[bot]</c> as the assignee and includes an
    /// <c>agent_assignment</c> payload to configure the base branch, custom agent,
    /// and additional instructions.
    /// </summary>
    private async Task AssignIssueToAgentAsync(int issueNumber, string baseBranch, CancellationToken cancellationToken)
    {
        // Map our agent name to the custom_agent field.
        // "copilot" (default) = no custom agent; "claude"/"codex" = partner agents.
        var customAgent = string.Equals(_agentAssignee, "copilot", StringComparison.OrdinalIgnoreCase)
            ? ""
            : _agentAssignee;

        // GitHub UI displays this as "Copilot", while legacy API examples often use
        // the bot login. Try both for compatibility across org feature rollouts.
        var assigneeCandidates = new[] { "copilot", "copilot-swe-agent[bot]" };
        HttpStatusCode? lastStatusCode = null;
        string? lastResponseBody = null;

        foreach (var assignee in assigneeCandidates)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["assignees"] = new[] { assignee },
                ["agent_assignment"] = new Dictionary<string, string>
                {
                    ["target_repo"] = $"{_githubOptions.Owner}/{_githubOptions.Repo}",
                    ["base_branch"] = baseBranch,
                    ["custom_instructions"] = "",
                    ["custom_agent"] = customAgent,
                    ["model"] = ""
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/assignees",
                content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Successfully assigned Issue #{IssueNumber} to {Assignee} (agent={Agent}, base={BaseBranch})",
                    issueNumber, assignee, _agentAssignee, baseBranch);
                return;
            }

            lastStatusCode = response.StatusCode;
            lastResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "Copilot assignment attempt failed for Issue #{IssueNumber} (assignee={Assignee}, agent={Agent}, status={StatusCode}): {Body}",
                issueNumber, assignee, _agentAssignee, response.StatusCode, lastResponseBody);
        }

        _logger.LogWarning(
            "Failed to assign Issue #{IssueNumber} to Copilot after all attempts (status={StatusCode}): {Body}. " +
            "Issue was created but may require manual start from GitHub UI.",
            issueNumber,
            lastStatusCode,
            lastResponseBody ?? "(no response body)");
    }

    private async Task PostKickoffCommentAsync(int issueNumber, CodingContext context, CancellationToken cancellationToken)
    {
        var kickoffComment =
            "@copilot Please start this implementation now. " +
            $"Use `{context.BranchName}` as the base branch and open a PR back to `{context.BranchName}` when complete.";

        var payload = new { body = kickoffComment };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/comments",
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to post Copilot kickoff comment for Issue #{IssueNumber} (status={StatusCode}): {Body}",
                issueNumber,
                response.StatusCode,
                body);
            return;
        }

        _logger.LogInformation("Posted Copilot kickoff comment for Issue #{IssueNumber}", issueNumber);
    }

    /// <summary>
    /// Builds the GitHub Issue body that serves as the prompt to Copilot.
    /// Includes the plan, acceptance criteria, coding guidelines, and explicit
    /// instructions about which branch to work on.
    /// </summary>
    internal static string BuildIssueBody(CodingContext context, string? agentName = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine($"Implement the changes for this story. The base branch is `{context.BranchName}`.");
        sb.AppendLine("Create your working branch from this base and target your PR back to it.");
        sb.AppendLine();
        sb.AppendLine($"> **ADO Work Item:** US-{context.WorkItemId}");
        sb.AppendLine($"> **Title:** {context.WorkItem.Title}");
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            sb.AppendLine($"> **Assigned Agent:** @{agentName}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(context.WorkItem.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(context.WorkItem.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(context.WorkItem.AcceptanceCriteria))
        {
            sb.AppendLine("## Acceptance Criteria");
            sb.AppendLine();
            sb.AppendLine(context.WorkItem.AcceptanceCriteria);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(context.StoryDocumentsFolder))
        {
            sb.AppendLine("## Story Supporting Files Folder");
            sb.AppendLine();
            sb.AppendLine("All supporting files from Azure DevOps are materialized in this branch folder. Review it before implementation:");
            sb.AppendLine($"- `{context.StoryDocumentsFolder}`");
            sb.AppendLine();
        }

        if (context.AttachedImagePaths.Count > 0)
        {
            sb.AppendLine("## Attached Visual References");
            sb.AppendLine();
            sb.AppendLine("The following story image attachments were materialized into the branch. Inspect them when implementing UI/layout changes:");
            foreach (var path in context.AttachedImagePaths)
            {
                sb.AppendLine($"- `{path}`");
            }
            sb.AppendLine();
        }

        if (context.AttachedDocumentPaths.Count > 0)
        {
            sb.AppendLine("## Attached Document References");
            sb.AppendLine();
            sb.AppendLine("The following supporting documents were materialized into the branch. Read them before implementing:");
            foreach (var path in context.AttachedDocumentPaths)
            {
                sb.AppendLine($"- `{path}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Implementation Plan");
        sb.AppendLine();
        sb.AppendLine(context.PlanMarkdown);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(context.CodingGuidelines))
        {
            sb.AppendLine("## Coding Guidelines");
            sb.AppendLine();
            sb.AppendLine(context.CodingGuidelines);
            sb.AppendLine();
        }

        sb.AppendLine("## Important Notes");
        sb.AppendLine();
        sb.AppendLine("- Follow the implementation plan above");
        sb.AppendLine("- Match existing code style and conventions");
        sb.AppendLine("- Do NOT modify test files, CI/CD workflows, or infrastructure (Terraform)");
        sb.AppendLine("- Ensure correct syntax, imports, and compilation");
        sb.AppendLine($"- Target branch for your PR: `{context.BranchName}`");

        return sb.ToString();
    }
}
