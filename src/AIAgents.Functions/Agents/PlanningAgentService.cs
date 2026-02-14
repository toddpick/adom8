using System.Text.Json;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Planning agent: analyzes the story, creates an implementation plan,
/// and renders it using the PLAN.template.md Scriban template.
/// Transitions: Story Planning → AI Code (enqueues Coding agent).
/// </summary>
public sealed class PlanningAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ITemplateEngine _templateEngine;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger<PlanningAgentService> _logger;
    private readonly string _storageConnectionString;

    public PlanningAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ITemplateEngine templateEngine,
        ICodebaseContextProvider codebaseContext,
        ILogger<PlanningAgentService> logger,
        IConfiguration configuration)
    {
        _aiClient = aiClientFactory.GetClientForAgent("Planning");
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _templateEngine = templateEngine;
        _codebaseContext = codebaseContext;
        _logger = logger;
        _storageConnectionString = configuration["AzureWebJobsStorage"]!;
    }

    public async Task ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Planning agent starting for WI-{WorkItemId}", task.WorkItemId);

        // 1. Get the work item details
        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);

        // 2. Ensure branch and get repo path
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        // 3. Get existing code context
        var existingFiles = await _gitOps.ListFilesAsync(repoPath, cancellationToken);
        var fileListSummary = string.Join("\n", existingFiles.Take(100));

        // 4. Create story context
        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "Story Planning";
        state.Agents["Planning"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        // 5. Call AI for planning analysis
        var systemPrompt = @"You are a senior software architect analyzing Azure DevOps user stories.
Analyze the story and produce a detailed implementation plan.
Respond ONLY with valid JSON matching this structure:
{
  ""problemAnalysis"": ""string"",
  ""technicalApproach"": ""string"",
  ""affectedFiles"": [""string""],
  ""complexity"": number (1-13 fibonacci),
  ""architecture"": ""string"",
  ""subTasks"": [""string""],
  ""dependencies"": [""string""],
  ""risks"": [""string""],
  ""assumptions"": [""string""],
  ""testingStrategy"": ""string""
}";

        var userPrompt = $@"## Story Details
**ID:** {workItem.Id}
**Title:** {workItem.Title}
**Description:** {workItem.Description ?? "No description provided"}
**Acceptance Criteria:** {workItem.AcceptanceCriteria ?? "No acceptance criteria"}
**Tags:** {string.Join(", ", workItem.Tags)}

## Existing Repository Files
{fileListSummary}

{await _codebaseContext.LoadRelevantContextAsync(repoPath, workItem.Title, workItem.Description, cancellationToken)}

Analyze this story and create a comprehensive implementation plan.";

        var aiResponse = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { Temperature = 0.3 }, cancellationToken);

        // 6. Parse AI response
        var planResult = ParsePlanningResult(aiResponse);

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

        // 11. Update ADO work item
        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>🤖 AI Planning Agent Complete</b><br/>Complexity: {planResult.Complexity} story points<br/>Sub-tasks: {planResult.SubTasks.Count}<br/>Risks: {planResult.Risks.Count}",
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

        // 13. Transition ADO state and enqueue next agent
        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "AI Code", cancellationToken);

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Coding,
            CorrelationId = task.CorrelationId
        };
        var queueClient = new QueueClient(_storageConnectionString, "agent-tasks");
        var messageJson = JsonSerializer.Serialize(nextTask);
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
        await queueClient.SendMessageAsync(base64, cancellationToken);

        _logger.LogInformation("Planning agent completed for WI-{WorkItemId}, enqueued Coding agent", task.WorkItemId);
    }

    private static PlanningResult ParsePlanningResult(string aiResponse)
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
                TestingStrategy = root.GetProperty("testingStrategy").GetString() ?? ""
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
}
