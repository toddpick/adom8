using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Services;

public sealed class GitHubOrchestrationKickoffResult
{
    public required int IssueNumber { get; init; }
    public required bool ExistingDelegation { get; init; }
    public required string AgentAssignee { get; init; }
}

public interface IGitHubOrchestrationLauncherService
{
    Task<GitHubOrchestrationKickoffResult> KickoffAsync(
        StoryWorkItem workItem,
        string correlationId,
        bool forceNewIssue,
        CancellationToken cancellationToken = default);

    Task CleanupForStoryStateAsync(
        int workItemId,
        string state,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubOrchestrationLauncherService : IGitHubOrchestrationLauncherService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> s_kickoffLocks = new();

    private readonly GitHubOptions _githubOptions;
    private readonly CopilotOptions _copilotOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly ILogger<GitHubOrchestrationLauncherService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _agentAssignee;
    private readonly IReadOnlyList<string> _additionalAssignees;
    private readonly string _adoProjectName;

    public GitHubOrchestrationLauncherService(
        IOptions<GitHubOptions> githubOptions,
        IOptions<CopilotOptions> copilotOptions,
        ICopilotDelegationService delegationService,
        ILogger<GitHubOrchestrationLauncherService> logger)
    {
        _githubOptions = githubOptions.Value;
        _copilotOptions = copilotOptions.Value;
        _delegationService = delegationService;
        _logger = logger;
        _agentAssignee = NormalizeAgentAssignee(_copilotOptions.Model);
        _additionalAssignees = ParseAdditionalAssignees(_copilotOptions.AdditionalAssignees);
        _adoProjectName =
            Environment.GetEnvironmentVariable("AZURE_DEVOPS_PROJECT")
            ?? Environment.GetEnvironmentVariable("AzureDevOps__Project")
            ?? Environment.GetEnvironmentVariable("AzureDevOps:Project")
            ?? "(configured Azure DevOps project)";

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

    public async Task<GitHubOrchestrationKickoffResult> KickoffAsync(
        StoryWorkItem workItem,
        string correlationId,
        bool forceNewIssue,
        CancellationToken cancellationToken = default)
    {
        var kickoffLock = s_kickoffLocks.GetOrAdd(workItem.Id, _ => new SemaphoreSlim(1, 1));
        await kickoffLock.WaitAsync(cancellationToken);

        try
        {
            int? supersededIssueNumber = null;

            var existingDelegation = await _delegationService.GetByWorkItemIdAsync(workItem.Id, cancellationToken);
            if (existingDelegation is not null && existingDelegation.Status == "Pending" && !forceNewIssue)
            {
                return new GitHubOrchestrationKickoffResult
                {
                    IssueNumber = existingDelegation.IssueNumber,
                    ExistingDelegation = true,
                    AgentAssignee = _agentAssignee
                };
            }

            if (existingDelegation is not null && existingDelegation.Status == "Pending" && forceNewIssue)
            {
                existingDelegation.Status = "Superseded";
                existingDelegation.CompletedAt = DateTime.UtcNow;
                await _delegationService.UpdateAsync(existingDelegation, cancellationToken);
                supersededIssueNumber = existingDelegation.IssueNumber;

                _logger.LogInformation(
                    "Superseded prior pending delegation for WI-{WorkItemId} (Issue #{IssueNumber}) before creating new kickoff issue",
                    workItem.Id,
                    existingDelegation.IssueNumber);
            }

            var branchName = $"feature/US-{workItem.Id}";
            var issueNumber = await CreateIssueAsync(workItem, branchName, cancellationToken);

            if (supersededIssueNumber.HasValue)
            {
                await PostSupersededCommentAsync(supersededIssueNumber.Value, issueNumber, cancellationToken);
            }

            await AssignIssueAsync(issueNumber, branchName, cancellationToken);
            await PostKickoffCommentAsync(issueNumber, branchName, cancellationToken);

            await _delegationService.CreateAsync(new CopilotDelegation
            {
                WorkItemId = workItem.Id,
                IssueNumber = issueNumber,
                CorrelationId = correlationId,
                BranchName = branchName,
                Status = "Pending"
            }, cancellationToken);

            _logger.LogInformation(
                "Created GitHub orchestration kickoff issue #{IssueNumber} for WI-{WorkItemId} assigned to @{Agent}",
                issueNumber,
                workItem.Id,
                _agentAssignee);

            return new GitHubOrchestrationKickoffResult
            {
                IssueNumber = issueNumber,
                ExistingDelegation = false,
                AgentAssignee = _agentAssignee
            };
        }
        finally
        {
            kickoffLock.Release();
            if (kickoffLock.CurrentCount == 1)
            {
                s_kickoffLocks.TryRemove(workItem.Id, out _);
            }
        }
    }

    private async Task<int> CreateIssueAsync(StoryWorkItem workItem, string branchName, CancellationToken cancellationToken)
    {
        var title = $"[US-{workItem.Id}] {workItem.Title}";
        var body = BuildIssueBody(workItem, branchName, _agentAssignee, _adoProjectName, new WorkItemSupportingArtifacts());
        var assigneeCandidates = BuildAssigneeCandidates(_agentAssignee);
        var primaryAssignee = assigneeCandidates.FirstOrDefault();

        var issuePayload = new Dictionary<string, object>
        {
            ["title"] = title,
            ["body"] = body,
            ["labels"] = new[] { "copilot-agent" }
        };

        if (!string.IsNullOrWhiteSpace(primaryAssignee))
        {
            issuePayload["assignees"] = new[] { primaryAssignee };
        }

        var content = new StringContent(JsonSerializer.Serialize(issuePayload), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues",
            content,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode &&
            response.StatusCode == HttpStatusCode.UnprocessableEntity &&
            issuePayload.ContainsKey("assignees") &&
            ShouldRetryIssueCreateWithoutAssignees(responseBody))
        {
            _logger.LogWarning(
                "GitHub issue create returned 422 with assignee payload for WI-{WorkItemId}; retrying without assignees. Body: {Body}",
                workItem.Id,
                responseBody);

            issuePayload.Remove("assignees");
            var retryContent = new StringContent(JsonSerializer.Serialize(issuePayload), Encoding.UTF8, "application/json");

            response = await _httpClient.PostAsync(
                $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues",
                retryContent,
                cancellationToken);

            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to create GitHub kickoff issue: {response.StatusCode} {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.GetProperty("number").GetInt32();
    }

    private async Task AssignIssueAsync(int issueNumber, string baseBranch, CancellationToken cancellationToken)
    {
        var customAgent = string.Equals(_agentAssignee, "copilot", StringComparison.OrdinalIgnoreCase)
            ? ""
            : _agentAssignee;

        var assigneeCandidates = BuildAssigneeCandidates(_agentAssignee);
        var assignmentAttempts = assigneeCandidates
            .SelectMany(assignee => new[]
            {
                new { Assignee = assignee, IncludeAgentAssignment = false, UsePatch = false, Name = $"post-plain-{assignee}" },
                new { Assignee = assignee, IncludeAgentAssignment = false, UsePatch = true, Name = $"patch-plain-{assignee}" },
                new { Assignee = assignee, IncludeAgentAssignment = true, UsePatch = false, Name = $"post-agent-assignment-{assignee}" },
                new { Assignee = assignee, IncludeAgentAssignment = true, UsePatch = true, Name = $"patch-agent-assignment-{assignee}" }
            })
            .ToList();

        HttpStatusCode? lastStatusCode = null;
        string? lastResponseBody = null;

        foreach (var attempt in assignmentAttempts)
        {
            Dictionary<string, object> requestBody;
            if (attempt.IncludeAgentAssignment)
            {
                var agentAssignment = new Dictionary<string, string>
                {
                    ["target_repo"] = $"{_githubOptions.Owner}/{_githubOptions.Repo}",
                    ["base_branch"] = baseBranch
                };

                if (!string.IsNullOrWhiteSpace(customAgent))
                {
                    agentAssignment["custom_agent"] = customAgent;
                }

                requestBody = new Dictionary<string, object>
                {
                    ["assignees"] = new[] { attempt.Assignee },
                    ["agent_assignment"] = agentAssignment
                };
            }
            else
            {
                requestBody = new Dictionary<string, object>
                {
                    ["assignees"] = new[] { attempt.Assignee }
                };
            }

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            if (attempt.UsePatch)
            {
                var patchRequest = new HttpRequestMessage(
                    HttpMethod.Patch,
                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}")
                {
                    Content = content
                };

                response = await _httpClient.SendAsync(patchRequest, cancellationToken);
            }
            else
            {
                response = await _httpClient.PostAsync(
                    $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/assignees",
                    content,
                    cancellationToken);
            }

            if (response.IsSuccessStatusCode)
            {
                var verified = await VerifyIssueAssignmentAsync(issueNumber, attempt.Assignee, cancellationToken);
                if (!verified)
                {
                    _logger.LogWarning(
                        "Assignment attempt returned success but issue #{IssueNumber} does not show expected assignee '{Assignee}' after {Attempt}. Continuing attempts.",
                        issueNumber,
                        attempt.Assignee,
                        attempt.Name);
                    continue;
                }

                await AssignAdditionalAssigneesAsync(issueNumber, cancellationToken);

                _logger.LogInformation(
                    "Successfully assigned orchestration issue #{IssueNumber} using {Attempt} (assignee={Assignee}, agent={Agent}, base={BaseBranch})",
                    issueNumber,
                    attempt.Name,
                    attempt.Assignee,
                    _agentAssignee,
                    baseBranch);
                return;
            }

            lastStatusCode = response.StatusCode;
            lastResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "Orchestration assignment attempt failed for issue #{IssueNumber} using {Attempt} (assignee={Assignee}, agent={Agent}, status={StatusCode}): {Body}",
                issueNumber,
                attempt.Name,
                attempt.Assignee,
                _agentAssignee,
                response.StatusCode,
                lastResponseBody);
        }

        _logger.LogWarning(
            "Failed to assign orchestration issue #{IssueNumber} after all attempts (status={StatusCode}): {Body}. Issue was created but may require manual start from GitHub UI.",
            issueNumber,
            lastStatusCode,
            lastResponseBody ?? "(no response body)");
    }

    private async Task<bool> VerifyIssueAssignmentAsync(
        int issueNumber,
        string expectedAssignee,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Could not verify orchestration issue assignment for issue #{IssueNumber} (status={StatusCode})",
                issueNumber,
                response.StatusCode);
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("assignees", out var assigneesElement) ||
            assigneesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignee in assigneesElement.EnumerateArray())
        {
            if (!assignee.TryGetProperty("login", out var loginEl))
            {
                continue;
            }

            var login = loginEl.GetString();
            if (!string.IsNullOrWhiteSpace(login))
            {
                logins.Add(login);
            }
        }

        return logins.Contains(expectedAssignee);
    }

    private async Task AssignAdditionalAssigneesAsync(int issueNumber, CancellationToken cancellationToken)
    {
        if (_additionalAssignees.Count == 0)
        {
            return;
        }

        var payload = new Dictionary<string, object>
        {
            ["assignees"] = _additionalAssignees
        };

        var response = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/assignees",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to assign additional assignees for orchestration issue #{IssueNumber}",
                issueNumber);
        }
    }

    private async Task PostKickoffCommentAsync(int issueNumber, string branchName, CancellationToken cancellationToken)
    {
        var comment =
            $"@{_agentAssignee} Start this story now using the full GitHub-agentic path. " +
            $"Use `{branchName}` as base branch. Read `.agent/ORCHESTRATION_CONTRACT.md` first and follow it exactly. " +
            "You own Planning through Documentation and MUST keep Azure DevOps synchronized via MCP `set-stage`, `add-comment`, and `stage-event` calls at each stage boundary. " +
            "When Documentation is complete, signal Deployment stage readiness via MCP and stop. Deployment execution is owned by Azure (Autonomy Level 5 only).";

        var payload = new { body = comment };
        var response = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/comments",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to post kickoff comment for orchestration issue #{IssueNumber}",
                issueNumber);
        }
    }

    private async Task PostSupersededCommentAsync(int oldIssueNumber, int newIssueNumber, CancellationToken cancellationToken)
    {
        var payload = new
        {
            body = $"This orchestration run was superseded after work item updates. Please continue in #{newIssueNumber}."
        };

        var response = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{oldIssueNumber}/comments",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to post superseded audit comment on issue #{OldIssueNumber} pointing to #{NewIssueNumber}",
                oldIssueNumber,
                newIssueNumber);
        }
    }

    private static string BuildIssueBody(
        StoryWorkItem workItem,
        string branchName,
        string agentAssignee,
        string adoProjectName,
        WorkItemSupportingArtifacts supportingArtifacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🚨 ADO NON-NEGOTIABLES (READ FIRST)");
        sb.AppendLine();
        sb.AppendLine("1. Use ONLY the `ado` MCP server for Azure DevOps updates.");
        sb.AppendLine("2. Before any repo/file/code work, post assignment acknowledgement comment (with task session id if available). Show MCP/API response.");
        sb.AppendLine($"3. Before any repo/file/code work, set `Custom.CurrentAIAgent = \"Planning Agent\"` and add `Entering Planning stage.` on work item `{workItem.Id}` in project `{adoProjectName}`. Show MCP/API response.");
        sb.AppendLine("4. For every stage, do stage-entry ADO updates first (`CurrentAIAgent` + `Entering [Stage] stage.`), then work.");
        sb.AppendLine("5. For every completed stage, post ADO completion evidence comment + stage event before moving on.");
        sb.AppendLine("6. Do NOT change ADO `System.State` during GitHub Planning/Coding/Testing/Review/Documentation progression unless explicitly required by contract.");
        sb.AppendLine();
        sb.AppendLine("## 🧾 Preflight ADO Acknowledgement (MUST DO FIRST)");
        sb.AppendLine();
        sb.AppendLine("Before any repo exploration or code changes, use the `ado` MCP server to add a comment on the ADO work item acknowledging assignment.");
        sb.AppendLine("Try to extract the GitHub task session id from your task URL (example: `.../tasks/605222de-71a9-4f99-8ee7-f9cceb0b6467`).");
        sb.AppendLine();
        sb.AppendLine("Required comment behavior:");
        sb.AppendLine("- If session id is available: `Thank you for assigning this task to me. I'm Copilot. Task session id: <session-id>`");
        sb.AppendLine("- If session id is not available: `Thank you for assigning this task to me. I'm Copilot.`");
        sb.AppendLine("Show the MCP/API response in the issue thread before proceeding.");
        sb.AppendLine();
        sb.AppendLine("## ⛔ HARD GATE (MUST DO FIRST)");
        sb.AppendLine();
        sb.AppendLine($"Use the `ado` MCP server to post `Custom.CurrentAIAgent = \"Planning Agent\"` and add comment `Entering Planning stage.` on ADO work item `{workItem.Id}` in project `{adoProjectName}`.");
        sb.AppendLine("Show the MCP/API response in the issue thread and do not proceed until this is confirmed.");
        sb.AppendLine();
        sb.AppendLine("## Mission");
        sb.AppendLine();
        sb.AppendLine("Run this work item through full GitHub-agent orchestration.");
        sb.AppendLine("You own stage execution from Planning through Documentation.");
        sb.AppendLine("Azure Functions are launcher/observer only until Deployment.");
        sb.AppendLine();
        sb.AppendLine("## Required Protocol");
        sb.AppendLine();
        sb.AppendLine("1. Read `.agent/ORCHESTRATION_CONTRACT.md` first and follow it as authoritative protocol.");
        sb.AppendLine("2. Keep Azure DevOps synchronized after every stage using MCP `set-stage`, `add-comment`, and `stage-event`.");
        sb.AppendLine("3. Execute stages in strict order: Planning → Coding → Testing → Review → Documentation.");
        sb.AppendLine("4. Do not perform deployment. Signal Deployment readiness via MCP when Documentation completes.");
        sb.AppendLine("5. Treat `AI Minimum Review Score` as the Planning readiness gate and route to Needs Revision when below threshold.");
        sb.AppendLine($"6. During Planning you MUST create/update `.ado/stories/US-{workItem.Id}/PLAN.md` and `.ado/stories/US-{workItem.Id}/TASKS.md` in the branch before moving to Coding.");
        sb.AppendLine("7. Stage-entry ADO gate is mandatory: before any work, set `Custom.CurrentAIAgent` for that stage and add comment `Entering [Stage] stage.`.");
        sb.AppendLine("8. Do not read files, explore repo, or write code until stage-entry ADO updates succeed. If MCP update fails, stop and report failure.");
        sb.AppendLine();
        sb.AppendLine($"> **ADO Work Item:** US-{workItem.Id}");
        sb.AppendLine($"> **Title:** {workItem.Title}");
        sb.AppendLine($"> **Assigned Agent:** @{agentAssignee}");
        sb.AppendLine($"> **AI Autonomy Level:** {workItem.AutonomyLevel}");
        sb.AppendLine($"> **AI Minimum Review Score:** {workItem.MinimumReviewScore}");
        sb.AppendLine($"> **Base Branch:** `{branchName}`");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(workItem.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(workItem.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(workItem.AcceptanceCriteria))
        {
            sb.AppendLine("## Acceptance Criteria");
            sb.AppendLine();
            sb.AppendLine(workItem.AcceptanceCriteria);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(supportingArtifacts.StoryDocumentsFolder))
        {
            sb.AppendLine("## Story Supporting Files Folder");
            sb.AppendLine();
            sb.AppendLine("All supporting files from Azure DevOps are materialized in this branch folder. Review it before implementation:");
            sb.AppendLine($"- `{supportingArtifacts.StoryDocumentsFolder}`");
            sb.AppendLine();
        }

        if (supportingArtifacts.ImagePaths.Count > 0)
        {
            sb.AppendLine("## Attached Visual References");
            sb.AppendLine();
            sb.AppendLine("The following story image attachments were materialized into the branch. Inspect them before implementation:");
            foreach (var path in supportingArtifacts.ImagePaths)
            {
                sb.AppendLine($"- `{path}`");
            }
            sb.AppendLine();
        }

        if (supportingArtifacts.DocumentPaths.Count > 0)
        {
            sb.AppendLine("## Attached Document References");
            sb.AppendLine();
            sb.AppendLine("The following supporting documents were materialized into the branch. Read them before implementation:");
            foreach (var path in supportingArtifacts.DocumentPaths)
            {
                sb.AppendLine($"- `{path}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Planning Artifacts (Required)");
        sb.AppendLine();
        sb.AppendLine("Before signaling Planning completion, create or update these files in the branch:");
        sb.AppendLine($"- `.ado/stories/US-{workItem.Id}/PLAN.md`");
        sb.AppendLine($"- `.ado/stories/US-{workItem.Id}/TASKS.md`");
        sb.AppendLine("These artifacts must reflect the latest readiness outcome and implementation plan.");
        sb.AppendLine();

        sb.AppendLine("## Completion");
        sb.AppendLine();
        sb.AppendLine("- Keep PR ready for review by end of Documentation stage.");
        sb.AppendLine("- Ensure all ADO stage comments/events are present with evidence.");
        sb.AppendLine("- Signal Deployment stage via MCP and stop.");
        return sb.ToString();
    }

    private static IReadOnlyList<string> BuildAssigneeCandidates(string? agentAssignee)
    {
        var normalized = NormalizeAgentAssignee(agentAssignee);

        if (normalized == "claude")
        {
            return new[] { "claude", "claude[bot]", "copilot" };
        }

        if (normalized == "codex")
        {
            return new[] { "codex", "codex[bot]", "copilot" };
        }

        return new[] { "copilot", "Copilot", "copilot-swe-agent", "copilot-swe-agent[bot]" };
    }

    private static string NormalizeAgentAssignee(string? agentAssignee)
    {
        if (string.IsNullOrWhiteSpace(agentAssignee))
        {
            return "copilot";
        }

        var value = agentAssignee.Trim().ToLowerInvariant();
        if (value.Contains("claude", StringComparison.Ordinal))
        {
            return "claude";
        }

        if (value.Contains("codex", StringComparison.Ordinal))
        {
            return "codex";
        }

        return "copilot";
    }

    private static IReadOnlyList<string> ParseAdditionalAssignees(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<string>();
        }

        return rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldRetryIssueCreateWithoutAssignees(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return true;
        }

        return responseBody.Contains("assignee", StringComparison.OrdinalIgnoreCase)
               || responseBody.Contains("could not resolve to a user", StringComparison.OrdinalIgnoreCase)
               || responseBody.Contains("invalid assignee", StringComparison.OrdinalIgnoreCase)
               || responseBody.Contains("unprocessable", StringComparison.OrdinalIgnoreCase);
    }

    public async Task CleanupForStoryStateAsync(
        int workItemId,
        string state,
        CancellationToken cancellationToken = default)
    {
        var delegation = await _delegationService.GetByWorkItemIdAsync(workItemId, cancellationToken);
        if (delegation is null || !string.Equals(delegation.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (delegation.IssueNumber > 0)
        {
            await CloseIssueAsync(delegation.IssueNumber, state, cancellationToken);
        }

        if (delegation.CopilotPrNumber is > 0)
        {
            await ClosePullRequestAsync(delegation.CopilotPrNumber.Value, state, cancellationToken);
        }

        delegation.Status = "ClosedByState";
        delegation.CompletedAt = DateTime.UtcNow;
        await _delegationService.UpdateAsync(delegation, cancellationToken);

        _logger.LogInformation(
            "Closed GitHub delegation artifacts for WI-{WorkItemId} due to state '{State}' (Issue #{IssueNumber}, PR #{PrNumber})",
            workItemId,
            state,
            delegation.IssueNumber,
            delegation.CopilotPrNumber ?? 0);
    }

    private async Task CloseIssueAsync(int issueNumber, string state, CancellationToken cancellationToken)
    {
        var commentPayload = new
        {
            body = $"Closing this delegated issue automatically because the linked work item moved to '{state}'."
        };

        _ = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}/comments",
            new StringContent(JsonSerializer.Serialize(commentPayload), Encoding.UTF8, "application/json"),
            cancellationToken);

        var closePayload = new { state = "closed" };
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{issueNumber}")
        {
            Content = new StringContent(JsonSerializer.Serialize(closePayload), Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to close issue #{IssueNumber} during state cleanup",
                issueNumber);
        }
    }

    private async Task ClosePullRequestAsync(int prNumber, string state, CancellationToken cancellationToken)
    {
        var issueCommentPayload = new
        {
            body = $"Closing this PR automatically because the linked work item moved to '{state}'."
        };

        _ = await _httpClient.PostAsync(
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/issues/{prNumber}/comments",
            new StringContent(JsonSerializer.Serialize(issueCommentPayload), Encoding.UTF8, "application/json"),
            cancellationToken);

        var closePayload = new { state = "closed" };
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"repos/{_githubOptions.Owner}/{_githubOptions.Repo}/pulls/{prNumber}")
        {
            Content = new StringContent(JsonSerializer.Serialize(closePayload), Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to close PR #{PrNumber} during state cleanup",
                prNumber);
        }
    }
}