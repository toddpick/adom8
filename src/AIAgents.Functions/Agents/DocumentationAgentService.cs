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
/// Documentation agent: generates documentation for the code changes.
/// Creates a PR with all artifacts when done.
/// Transitions: AI Docs → Deployment (enqueues DeploymentAgent for merge/deploy decisions).
/// </summary>
public sealed class DocumentationAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IRepositoryProvider _repoProvider;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ITemplateEngine _templateEngine;
    private readonly ILogger<DocumentationAgentService> _logger;
    private readonly string _storageConnectionString;

    public DocumentationAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IRepositoryProvider repoProvider,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ITemplateEngine templateEngine,
        ILogger<DocumentationAgentService> logger,
        IConfiguration configuration)
    {
        _aiClient = aiClientFactory.GetClientForAgent("Documentation");
        _adoClient = adoClient;
        _repoProvider = repoProvider;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _templateEngine = templateEngine;
        _logger = logger;
        _storageConnectionString = configuration["AzureWebJobsStorage"]!;
    }

    public async Task ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Documentation agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "AI Docs";
        state.Agents["Documentation"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        // Gather all artifacts
        var plan = await context.ReadArtifactAsync("PLAN.md", cancellationToken) ?? "";
        var review = await context.ReadArtifactAsync("CODE_REVIEW.md", cancellationToken) ?? "";

        var codeContents = new List<string>();
        foreach (var path in state.Artifacts.Code)
        {
            var content = await _gitOps.ReadFileAsync(repoPath, path, cancellationToken);
            if (content is not null)
                codeContents.Add($"// File: {path}\n{content}");
        }
        var allCode = string.Join("\n\n", codeContents);

        // AI documentation generation
        var systemPrompt = @"You are a technical writer creating comprehensive documentation.
Based on the code changes, plan, and review, generate documentation.
Respond ONLY with valid JSON:
{
  ""overview"": ""string"",
  ""changes"": ""string (markdown)"",
  ""apiDocs"": ""string (markdown)"",
  ""usageExamples"": ""string (markdown)"",
  ""breakingChanges"": ""string or null"",
  ""migrationGuide"": ""string or null"",
  ""configurationChanges"": ""string (markdown)""
}";

        var userPrompt = $@"## Story
**ID:** {workItem.Id}
**Title:** {workItem.Title}
**Description:** {workItem.Description ?? "N/A"}

## Implementation Plan
{plan}

## Code Review
{review}

## Generated Code
{allCode}

Generate comprehensive documentation for these changes.";

        var aiResponse = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 4096, Temperature = 0.3 }, cancellationToken);

        var docResult = ParseDocResult(aiResponse);

        // Render documentation template
        var templateModel = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = $"US-{workItem.Id}",
            ["TITLE"] = workItem.Title,
            ["TIMESTAMP"] = DateTime.UtcNow.ToString("O"),
            ["OVERVIEW"] = docResult.Overview,
            ["CHANGES"] = docResult.Changes,
            ["API_DOCS"] = docResult.ApiDocs,
            ["USAGE_EXAMPLES"] = docResult.UsageExamples,
            ["BREAKING_CHANGES"] = docResult.BreakingChanges,
            ["MIGRATION_GUIDE"] = docResult.MigrationGuide,
            ["CONFIGURATION_CHANGES"] = docResult.ConfigurationChanges
        };

        var renderedDocs = await _templateEngine.RenderAsync("DOCUMENTATION.template.md", templateModel, cancellationToken);
        await context.WriteArtifactAsync("DOCUMENTATION.md", renderedDocs, cancellationToken);
        state.Artifacts.Docs.Add($".ado/stories/US-{workItem.Id}/DOCUMENTATION.md");

        // Final commit
        await _gitOps.CommitAndPushAsync(repoPath,
            $"[AI Docs] US-{workItem.Id}: Documentation generated", cancellationToken);

        // Create pull request
        var prDescription = $@"## AI-Generated Changes for US-{workItem.Id}

**Story:** {workItem.Title}

### Pipeline Summary
- **Planning:** ✅ Complete
- **Coding:** ✅ {state.Artifacts.Code.Count} file(s) generated
- **Testing:** ✅ {state.Artifacts.Tests.Count} test file(s) generated
- **Review:** ✅ Complete
- **Documentation:** ✅ Complete

### Files Changed
{string.Join("\n", state.Artifacts.Code.Select(f => $"- `{f}`"))}

### Test Files
{string.Join("\n", state.Artifacts.Tests.Select(f => $"- `{f}`"))}

---
*Generated by AI Agent Pipeline*";

        var prId = await _repoProvider.CreatePullRequestAsync(
            sourceBranch: branchName,
            targetBranch: "main",
            title: $"US-{workItem.Id}: {workItem.Title}",
            description: prDescription,
            cancellationToken: cancellationToken);

        // Update ADO
        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>\ud83e\udd16 AI Documentation Agent Complete</b><br/>Pull Request: <a href=\"#\">PR #{prId}</a><br/>All code-generation agents completed. Handing off to Deployment agent.",
            cancellationToken);

        // Store PR ID in state so DeploymentAgent can access it
        state.Agents["Documentation"] = AgentStatus.Completed();
        state.Agents["Documentation"].AdditionalData = new Dictionary<string, object>
        {
            ["prId"] = prId
        };
        state.CurrentState = "AI Deployment";
        await context.SaveStateAsync(state, cancellationToken);

        // Enqueue Deployment agent (handles merge/deploy based on autonomy level)
        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Deployment,
            CorrelationId = task.CorrelationId
        };
        var queueClient = new QueueClient(_storageConnectionString, "agent-tasks");
        var messageJson = JsonSerializer.Serialize(nextTask);
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
        await queueClient.SendMessageAsync(base64, cancellationToken);

        _logger.LogInformation(
            "Documentation agent completed for WI-{WorkItemId}. PR #{PrId} created. Enqueued Deployment agent.",
            task.WorkItemId, prId);
    }

    private static string ExtractRepoName(string repoPath)
    {
        // Extract repo name from path (last directory component)
        return new DirectoryInfo(repoPath).Name;
    }

    private static DocResult ParseDocResult(string aiResponse)
    {
        var json = aiResponse.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                json = json[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new DocResult
            {
                Overview = root.GetProperty("overview").GetString() ?? "",
                Changes = root.GetProperty("changes").GetString() ?? "",
                ApiDocs = root.GetProperty("apiDocs").GetString() ?? "",
                UsageExamples = root.GetProperty("usageExamples").GetString() ?? "",
                BreakingChanges = root.TryGetProperty("breakingChanges", out var bc) && bc.ValueKind == JsonValueKind.String ? bc.GetString() : null,
                MigrationGuide = root.TryGetProperty("migrationGuide", out var mg) && mg.ValueKind == JsonValueKind.String ? mg.GetString() : null,
                ConfigurationChanges = root.GetProperty("configurationChanges").GetString() ?? "None"
            };
        }
        catch (JsonException)
        {
            return new DocResult
            {
                Overview = aiResponse,
                Changes = "See overview",
                ApiDocs = "To be documented",
                UsageExamples = "To be documented",
                ConfigurationChanges = "None"
            };
        }
    }

    private sealed class DocResult
    {
        public string Overview { get; init; } = "";
        public string Changes { get; init; } = "";
        public string ApiDocs { get; init; } = "";
        public string UsageExamples { get; init; } = "";
        public string? BreakingChanges { get; init; }
        public string? MigrationGuide { get; init; }
        public string ConfigurationChanges { get; init; } = "None";
    }
}
