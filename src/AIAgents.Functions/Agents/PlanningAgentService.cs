using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Planning agent: analyzes the story, creates an implementation plan,
/// and renders it using the PLAN.template.md Scriban template.
/// Transitions: Story Planning → AI Code (enqueues Coding agent).
/// </summary>
public sealed class PlanningAgentService : IAgentService
{
    private readonly IAIClientFactory _aiClientFactory;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ITemplateEngine _templateEngine;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger<PlanningAgentService> _logger;
    private readonly IAgentTaskQueue _taskQueue;

    public PlanningAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ITemplateEngine templateEngine,
        ICodebaseContextProvider codebaseContext,
        ILogger<PlanningAgentService> logger,
        IAgentTaskQueue taskQueue)
    {
        _aiClientFactory = aiClientFactory;
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _templateEngine = templateEngine;
        _codebaseContext = codebaseContext;
        _logger = logger;
        _taskQueue = taskQueue;
    }

    public async Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        try
        {
        _logger.LogInformation("Planning agent starting for WI-{WorkItemId}", task.WorkItemId);

        // 1. Get the work item details
        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);

        // 1b. Resolve AI client with per-story model overrides
        var aiClient = _aiClientFactory.GetClientForAgent("Planning", workItem.GetModelOverrides());

        // 2. Ensure branch and get repo path
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        // 2b. Materialize supporting files (images/documents) into the story workspace/repo
        var supportingArtifacts = await _adoClient.DownloadSupportingArtifactsAsync(task.WorkItemId, repoPath, cancellationToken);

        // 3. Get existing code context
        var existingFiles = await _gitOps.ListFilesAsync(repoPath, cancellationToken);
        var fileListSummary = string.Join("\n", existingFiles.Take(100));

        // 4. Create story context
        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "Story Planning";
        state.Agents["Planning"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        // 5. Detect placeholder text before calling AI
        var placeholderWarning = DetectPlaceholders(workItem);

        // 6. Call AI for planning analysis with triage gate
        var systemPrompt = @"You are a senior software architect analyzing Azure DevOps user stories.
You have TWO jobs:
1. TRIAGE — Assess whether the story is ready for AI coding.
2. PLAN — If ready, create a detailed implementation plan.

TRIAGE CHECKS (evaluate all 7):
- Completeness: Does the story have a clear title, description, AND acceptance criteria? All three are REQUIRED. An empty or missing acceptance criteria is an automatic blocker.
- Complexity: Is the estimated complexity ≤ 13 story points? Stories over 13 should be broken down.
- Ambiguity: Are the requirements specific enough to implement without guessing? Flag vague terms like 'improve', 'optimize', 'make better', 'enhance' without measurable criteria.
- Risk: Are there architectural risks that need human review first?
- Feasibility: Can this be implemented with the existing codebase and tech stack?
- Content Quality: Does the story contain placeholder or unresolved text? Look for: TBD, TODO, TBC, N/A (in required fields), 'need to decide', 'to be determined', 'not yet defined', 'undecided', 'placeholder', '[fill in]', '???', or any open questions/decision points that haven't been resolved. Each placeholder found is a blocker — list the exact text and which field it appears in.
- Unverified Assumptions: Does the story make assumptions about external API capabilities that cannot be verified from codebase documentation alone? Flag assumptions about:
  • GitHub API capabilities (task GUIDs, repository metadata, Copilot-specific features, webhook payloads, GraphQL fields)
  • Azure DevOps API capabilities (custom fields, work item types, webhooks, process templates, specific field names not in standard docs)
  • Third-party APIs (specific endpoints, data formats, authentication methods, rate limits)
  • Any external data that the story assumes exists or can be retrieved but hasn't been verified in codebase documentation or comments

If ANY check fails, set readiness.proceed=false with clear blockers and questions.
Be STRICT: do not allow stories with unverified decisions or placeholder text to proceed.

DISTINCTION between questions and researchNeeded:
- questions: Human clarification needed (business logic, requirements, user experience decisions)
- researchNeeded: Technical verification needed (external API capabilities, data availability, endpoint existence)

Respond ONLY with valid JSON matching this structure:
{
  ""readiness"": {
    ""proceed"": true/false,
    ""readinessScore"": number (0-100),
    ""blockers"": [""string — blocking issue""],
    ""questions"": [""string — question for the analyst requiring human clarification""],
    ""researchNeeded"": [""string — unverified external API assumption that needs technical investigation""],
    ""suggestedBreakdown"": [""string — suggested sub-stories if too complex""],
    ""reason"": ""brief rationale for the decision""
  },
  ""problemAnalysis"": ""string"",
  ""technicalApproach"": ""string"",
  ""affectedFiles"": [""string""],
  ""complexity"": number (1-13 fibonacci scale),
  ""architecture"": ""string"",
  ""subTasks"": [""string""],
  ""dependencies"": [""string""],
  ""risks"": [""string""],
  ""assumptions"": [""string""],
  ""testingStrategy"": ""string""
}

ALWAYS include the full plan even when proceed=false — the analyst needs the analysis to fix the story.
If unverified external dependencies are detected, add a warning note in the technicalApproach field.";

        var userPrompt = $@"## Story Details
**ID:** {workItem.Id}
**Title:** {workItem.Title}
**Description:** {workItem.Description ?? "No description provided"}
**Acceptance Criteria:** {workItem.AcceptanceCriteria ?? "No acceptance criteria"}
**Tags:** {string.Join(", ", workItem.Tags)}
{placeholderWarning}
    {(supportingArtifacts.AllPaths.Count > 0
        ? $"## Supporting Documents\nAll supporting files for this story were materialized under `{supportingArtifacts.StoryDocumentsFolder}`. Inspect these before planning implementation:\n{string.Join("\n", supportingArtifacts.AllPaths.Select(path => $"- {path}"))}\n"
        : string.Empty)}
## Existing Repository Files
{fileListSummary}

{await _codebaseContext.LoadRelevantContextAsync(repoPath, workItem.Title, workItem.Description, cancellationToken)}

Analyze this story and create a comprehensive implementation plan.";

        var aiResult = await aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { Temperature = 0.3 }, cancellationToken);
        state.TokenUsage.RecordUsage("Planning", aiResult.Usage);

        // 6. Parse AI response
        var planResult = ParsePlanningResult(aiResult.Content);

        // 7. Render plan template
        var templateModel = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = $"US-{workItem.Id}",
            ["TITLE"] = workItem.Title,
            ["STATE"] = workItem.State,
            ["CREATED_DATE"] = workItem.CreatedDate.ToString("yyyy-MM-dd"),
            ["DESCRIPTION"] = workItem.Description ?? "No description provided",
            ["ACCEPTANCE_CRITERIA"] = workItem.AcceptanceCriteria ?? "No acceptance criteria",
            ["PROBLEM_ANALYSIS"] = planResult.ProblemAnalysis,
            ["TECHNICAL_APPROACH"] = planResult.TechnicalApproach,
            ["AFFECTED_FILES"] = planResult.AffectedFiles,
            ["COMPLEXITY"] = planResult.Complexity,
            ["ARCHITECTURE"] = planResult.Architecture,
            ["SUBTASKS"] = planResult.SubTasks,
            ["DEPENDENCIES"] = planResult.Dependencies,
            ["RISKS"] = planResult.Risks,
            ["ASSUMPTIONS"] = planResult.Assumptions,
            ["TESTING_STRATEGY"] = planResult.TestingStrategy,
            ["TIMESTAMP"] = DateTime.UtcNow.ToString("O")
        };

        var renderedPlan = await _templateEngine.RenderAsync("PLAN.template.md", templateModel, cancellationToken);

        // 8. Save artifacts
        await context.WriteArtifactAsync("PLAN.md", renderedPlan, cancellationToken);
        await _gitOps.WriteFileAsync(repoPath, $".ado/stories/US-{workItem.Id}/PLAN.md", renderedPlan, cancellationToken);

        // 9. Render and save tasks
        var tasksModel = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = $"US-{workItem.Id}",
            ["TITLE"] = workItem.Title,
            ["SUBTASKS"] = planResult.SubTasks,
            ["TIMESTAMP"] = DateTime.UtcNow.ToString("O")
        };
        var renderedTasks = await _templateEngine.RenderAsync("TASKS.template.md", tasksModel, cancellationToken);
        await context.WriteArtifactAsync("TASKS.md", renderedTasks, cancellationToken);

        // 10. Commit and push
        await _gitOps.CommitAndPushAsync(repoPath,
            $"[AI Planning] US-{workItem.Id}: {workItem.Title}", cancellationToken);

        // 12. Triage gate — check readiness
        var readiness = planResult.Readiness;
        if (readiness is not null && !readiness.Proceed)
        {
            // Story is NOT ready — send back with feedback
            _logger.LogInformation(
                "Planning agent REJECTED WI-{WorkItemId}: score={Score}, blockers={Blockers}",
                task.WorkItemId, readiness.ReadinessScore, readiness.Blockers.Count);

            var blockerList = readiness.Blockers.Count > 0
                ? string.Join("<br/>", readiness.Blockers.Select(b => $"❌ {b}"))
                : "None";
            var questionList = readiness.Questions.Count > 0
                ? string.Join("<br/>", readiness.Questions.Select(q => $"❓ {q}"))
                : "None";
            var researchList = readiness.ResearchNeeded.Count > 0
                ? string.Join("<br/>", readiness.ResearchNeeded.Select(r => $"🔍 {r}"))
                : "";
            var breakdownList = readiness.SuggestedBreakdown.Count > 0
                ? string.Join("<br/>", readiness.SuggestedBreakdown.Select(s => $"📋 {s}"))
                : "";

            var rejectComment = $"<b>🚫 AI Planning Agent — Story Not Ready for Coding</b><br/>" +
                $"<b>Readiness Score:</b> {readiness.ReadinessScore}/100<br/>" +
                $"<b>Reason:</b> {System.Net.WebUtility.HtmlEncode(readiness.Reason)}<br/><br/>" +
                $"<b>Blockers:</b><br/>{blockerList}<br/><br/>" +
                $"<b>Questions to Answer:</b><br/>{questionList}";

            if (readiness.ResearchNeeded.Count > 0)
                rejectComment += $"<br/><br/><b>Research Needed (Unverified External API Assumptions):</b><br/>{researchList}";

            if (readiness.SuggestedBreakdown.Count > 0)
                rejectComment += $"<br/><br/><b>Suggested Breakdown:</b><br/>{breakdownList}";

            rejectComment += $"<br/><br/><b>Complexity Estimate:</b> {planResult.Complexity} story points" +
                $"<br/><b>Risks:</b> {string.Join(", ", planResult.Risks)}" +
                $"<br/><br/><i>Please address the blockers and questions above, then move the story back to 'Story Planning' to re-trigger the pipeline.</i>";

            await _adoClient.AddWorkItemCommentAsync(workItem.Id, rejectComment, cancellationToken);

            // Update state — mark planning as completed with rejection info
            state.Agents["Planning"] = AgentStatus.Completed();
            state.Agents["Planning"].AdditionalData = new Dictionary<string, object>
            {
                ["triageResult"] = "rejected",
                ["readinessScore"] = readiness.ReadinessScore,
                ["blockerCount"] = readiness.Blockers.Count
            };
            state.CurrentState = "Needs Revision";
            state.Decisions.Add(new Decision
            {
                Agent = "Planning",
                DecisionText = $"Story rejected by triage gate (score: {readiness.ReadinessScore}/100)",
                Rationale = readiness.Reason
            });
            await context.SaveStateAsync(state, cancellationToken);

            try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.LastAgent, "Planning", cancellationToken); }
            catch { /* field may not exist yet */ }

            // Move story to Needs Revision — do NOT enqueue Coding agent
            await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "Needs Revision", cancellationToken);

            _logger.LogInformation("Planning agent rejected WI-{WorkItemId}, moved to Needs Revision", task.WorkItemId);
            return AgentResult.Ok(aiResult.Usage?.TotalTokens ?? 0, aiResult.Usage?.EstimatedCost ?? 0m);
        }

        // 13. Story IS ready — proceed to coding
        var readinessInfo = readiness is not null
            ? $"<br/>Readiness: {readiness.ReadinessScore}/100"
            : "";

        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>🤖 AI Planning Agent Complete</b><br/>Complexity: {planResult.Complexity} story points<br/>Sub-tasks: {planResult.SubTasks.Count}<br/>Risks: {planResult.Risks.Count}{readinessInfo}",
            cancellationToken);

        // 12. Update story state
        state.Agents["Planning"] = AgentStatus.Completed();
        state.CurrentState = "AI Code";
        state.Decisions.Add(new Decision
        {
            Agent = "Planning",
            DecisionText = $"Estimated complexity: {planResult.Complexity} story points",
            Rationale = planResult.TechnicalApproach
        });
        await context.SaveStateAsync(state, cancellationToken);

        // Track last agent in ADO
        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.LastAgent, "Planning", cancellationToken); }
        catch { /* field may not exist yet */ }

        // 13. Transition ADO state and enqueue next agent
        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "AI Code", cancellationToken);

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Coding,
            CorrelationId = task.CorrelationId
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        _logger.LogInformation("Planning agent completed for WI-{WorkItemId}, enqueued Coding agent", task.WorkItemId);

            return AgentResult.Ok(aiResult.Usage?.TotalTokens ?? 0, aiResult.Usage?.EstimatedCost ?? 0m);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"Rate limit hit for Planning agent on WI-{task.WorkItemId}", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AgentResult.Fail(ErrorCategory.Configuration, $"Authentication failed for Planning agent on WI-{task.WorkItemId}. Check API key.", ex);
        }
        catch (HttpRequestException ex)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"HTTP error in Planning agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(ErrorCategory.Code, $"Unexpected error in Planning agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
    }

    internal static PlanningResult ParsePlanningResult(string aiResponse)
    {
        // Strip markdown code fences if present
        var json = aiResponse.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                json = json[(firstNewline + 1)..lastFence].Trim();
            }
        }

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Parse readiness assessment if present
            PlanningReadiness? readiness = null;
            if (root.TryGetProperty("readiness", out var readinessEl) && readinessEl.ValueKind == JsonValueKind.Object)
            {
                readiness = new PlanningReadiness
                {
                    Proceed = readinessEl.TryGetProperty("proceed", out var p) && p.GetBoolean(),
                    ReadinessScore = readinessEl.TryGetProperty("readinessScore", out var rs) ? rs.GetInt32() : (readinessEl.TryGetProperty("proceed", out var p2) && p2.GetBoolean() ? 100 : 0),
                    Blockers = GetStringArray(readinessEl, "blockers"),
                    Questions = GetStringArray(readinessEl, "questions"),
                    ResearchNeeded = GetStringArray(readinessEl, "researchNeeded"),
                    SuggestedBreakdown = GetStringArray(readinessEl, "suggestedBreakdown"),
                    Reason = readinessEl.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""
                };
            }

            return new PlanningResult
            {
                ProblemAnalysis = root.GetProperty("problemAnalysis").GetString() ?? "",
                TechnicalApproach = root.GetProperty("technicalApproach").GetString() ?? "",
                AffectedFiles = GetStringArray(root, "affectedFiles"),
                Complexity = root.TryGetProperty("complexity", out var c) ? c.GetInt32() : 5,
                Architecture = root.GetProperty("architecture").GetString() ?? "",
                SubTasks = GetStringArray(root, "subTasks"),
                Dependencies = GetStringArray(root, "dependencies"),
                Risks = GetStringArray(root, "risks"),
                Assumptions = GetStringArray(root, "assumptions"),
                TestingStrategy = root.GetProperty("testingStrategy").GetString() ?? "",
                Readiness = readiness
            };
        }
        catch (JsonException)
        {
            // Fallback: treat the entire response as the analysis
            return new PlanningResult
            {
                ProblemAnalysis = aiResponse,
                TechnicalApproach = "See analysis above",
                AffectedFiles = [],
                Complexity = 5,
                Architecture = "To be determined",
                SubTasks = ["Review AI analysis", "Implement changes", "Write tests"],
                Dependencies = [],
                Risks = ["AI response could not be parsed as structured JSON"],
                Assumptions = [],
                TestingStrategy = "Unit and integration tests recommended"
            };
        }
    }

    private static List<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return [];

        return prop.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    /// <summary>
    /// Scans story fields for placeholder/unresolved text patterns.
    /// Returns a warning string to inject into the AI prompt, or empty if clean.
    /// </summary>
    internal static string DetectPlaceholders(StoryWorkItem workItem)
    {
        var placeholderPattern = new Regex(
            @"\bTBD\b|\bTODO\b|\bTBC\b|\bN/?A\b|\bplaceholder\b|\bundecided\b|\bneed\s+to\s+decide\b|to\s+be\s+determined|not\s+yet\s+defined|\[fill\s*in\]|\?\?\?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var findings = new List<string>();

        ScanField("Title", workItem.Title, placeholderPattern, findings);
        ScanField("Description", workItem.Description, placeholderPattern, findings);
        ScanField("Acceptance Criteria", workItem.AcceptanceCriteria, placeholderPattern, findings);

        if (findings.Count == 0)
            return "";

        var warning = "\n## ⚠️ PLACEHOLDER TEXT DETECTED\n" +
            "The following placeholder/unresolved text was found in the story fields. " +
            "These MUST be treated as blockers in your triage assessment (set proceed=false):\n" +
            string.Join("\n", findings.Select(f => $"- {f}"));

        return warning;
    }

    private static void ScanField(string fieldName, string? value, Regex pattern, List<string> findings)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        // Strip HTML tags for cleaner scanning
        var plainText = Regex.Replace(value, @"<[^>]+>", " ");

        foreach (Match match in pattern.Matches(plainText))
        {
            findings.Add($"{fieldName}: found \"{match.Value}\" near \"...{GetSurrounding(plainText, match.Index, 30)}...\"");
        }
    }

    private static string GetSurrounding(string text, int index, int radius)
    {
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + radius);
        return text[start..end].Trim();
    }
}
