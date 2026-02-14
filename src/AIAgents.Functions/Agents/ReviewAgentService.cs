using System.Text.Json;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Review agent: performs AI code review on generated code.
/// Scores code quality, identifies security issues, and provides recommendations.
/// Transitions: AI Review → AI Docs (enqueues Documentation agent).
/// </summary>
public sealed class ReviewAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ITemplateEngine _templateEngine;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger<ReviewAgentService> _logger;
    private readonly IAgentTaskQueue _taskQueue;

    public ReviewAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ITemplateEngine templateEngine,
        ICodebaseContextProvider codebaseContext,
        ILogger<ReviewAgentService> logger,
        IAgentTaskQueue taskQueue)
    {
        _aiClient = aiClientFactory.GetClientForAgent("Review");
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _templateEngine = templateEngine;
        _codebaseContext = codebaseContext;
        _logger = logger;
        _taskQueue = taskQueue;
    }

    public async Task ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Review agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "AI Review";
        state.Agents["Review"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        // Read all code and test files
        var allPaths = state.Artifacts.Code.Concat(state.Artifacts.Tests).ToList();
        var fileContents = new List<string>();
        foreach (var path in allPaths)
        {
            var content = await _gitOps.ReadFileAsync(repoPath, path, cancellationToken);
            if (content is not null)
                fileContents.Add($"// File: {path}\n{content}");
        }
        var allCode = string.Join("\n\n---\n\n", fileContents);

        // AI review
        var systemPrompt = @"You are a senior code reviewer performing a thorough security and quality review.
Respond ONLY with valid JSON:
{
  ""score"": number (0-100),
  ""recommendation"": ""Approve|Approve with Comments|Request Changes|Reject"",
  ""summary"": ""string"",
  ""criticalIssues"": [{ ""line"": number|null, ""issue"": ""string"", ""fix"": ""string"", ""code"": ""string|null"" }],
  ""highIssues"": [{ ""line"": number|null, ""issue"": ""string"", ""fix"": ""string"" }],
  ""mediumIssues"": [{ ""issue"": ""string"" }],
  ""lowIssues"": [{ ""issue"": ""string"" }],
  ""positiveFindings"": [""string""]
}
Check for: SQL injection, XSS, hardcoded secrets, null reference, race conditions, error handling, SOLID violations, and performance issues.";

        var userPrompt = $@"## Story
**ID:** {workItem.Id}
**Title:** {workItem.Title}

## Code to Review
{allCode}

{await _codebaseContext.LoadRelevantContextAsync(repoPath, workItem.Title, workItem.Description, cancellationToken)}

Perform a comprehensive code review.";

        var aiResult = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 4096, Temperature = 0.2 }, cancellationToken);
        state.TokenUsage.RecordUsage("Review", aiResult.Usage);

        var reviewResult = ParseReviewResult(aiResult.Content);

        // Render review template
        var templateModel = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = $"US-{workItem.Id}",
            ["SCORE"] = reviewResult.Score,
            ["RECOMMENDATION"] = reviewResult.Recommendation,
            ["SUMMARY"] = reviewResult.Summary,
            ["CRITICAL_COUNT"] = reviewResult.CriticalIssues.Count,
            ["CRITICAL_ISSUES"] = reviewResult.CriticalIssues.Select(i => new Dictionary<string, object?>
            {
                ["LINE"] = i.Line?.ToString() ?? "N/A",
                ["ISSUE"] = i.Issue,
                ["FIX"] = i.Fix ?? "",
                ["CODE"] = i.Code
            }).ToList(),
            ["HIGH_COUNT"] = reviewResult.HighIssues.Count,
            ["HIGH_ISSUES"] = reviewResult.HighIssues.Select(i => new Dictionary<string, object?>
            {
                ["LINE"] = i.Line?.ToString() ?? "N/A",
                ["ISSUE"] = i.Issue,
                ["FIX"] = i.Fix ?? ""
            }).ToList(),
            ["MEDIUM_COUNT"] = reviewResult.MediumIssues.Count,
            ["MEDIUM_ISSUES"] = reviewResult.MediumIssues.Select(i => new Dictionary<string, object?>
            {
                ["ISSUE"] = i.Issue
            }).ToList(),
            ["LOW_COUNT"] = reviewResult.LowIssues.Count,
            ["LOW_ISSUES"] = reviewResult.LowIssues.Select(i => new Dictionary<string, object?>
            {
                ["ISSUE"] = i.Issue
            }).ToList(),
            ["POSITIVE_FINDINGS"] = reviewResult.PositiveFindings.ToList(),
            ["MODEL_NAME"] = "AI Review Agent",
            ["TIMESTAMP"] = DateTime.UtcNow.ToString("O")
        };

        var renderedReview = await _templateEngine.RenderAsync("CODE_REVIEW.template.md", templateModel, cancellationToken);
        await context.WriteArtifactAsync("CODE_REVIEW.md", renderedReview, cancellationToken);

        // Commit
        await _gitOps.CommitAndPushAsync(repoPath,
            $"[AI Review] US-{workItem.Id}: Score {reviewResult.Score}/100 - {reviewResult.Recommendation}",
            cancellationToken);

        // Update ADO
        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>🤖 AI Review Agent Complete</b><br/>Score: {reviewResult.Score}/100<br/>Recommendation: {reviewResult.Recommendation}<br/>Critical: {reviewResult.CriticalIssues.Count} | High: {reviewResult.HighIssues.Count}",
            cancellationToken);

        // Update state and enqueue next
        state.Agents["Review"] = AgentStatus.Completed();
        state.Agents["Review"].AdditionalData = new Dictionary<string, object>
        {
            ["score"] = reviewResult.Score,
            ["recommendation"] = reviewResult.Recommendation
        };
        state.CurrentState = "AI Docs";
        await context.SaveStateAsync(state, cancellationToken);

        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "AI Docs", cancellationToken);

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Documentation,
            CorrelationId = task.CorrelationId
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        _logger.LogInformation("Review agent completed for WI-{WorkItemId}, enqueued Documentation agent", task.WorkItemId);
    }

    private static CodeReviewResult ParseReviewResult(string aiResponse)
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

            return new CodeReviewResult
            {
                Score = root.TryGetProperty("score", out var s) ? s.GetInt32() : 50,
                Recommendation = root.GetProperty("recommendation").GetString() ?? "Review Required",
                Summary = root.GetProperty("summary").GetString() ?? "",
                CriticalIssues = ParseIssues(root, "criticalIssues"),
                HighIssues = ParseIssues(root, "highIssues"),
                MediumIssues = ParseIssues(root, "mediumIssues"),
                LowIssues = ParseIssues(root, "lowIssues"),
                PositiveFindings = GetStringArray(root, "positiveFindings")
            };
        }
        catch (JsonException)
        {
            return new CodeReviewResult
            {
                Score = 50,
                Recommendation = "Review Required",
                Summary = "AI response could not be parsed. Manual review required.",
                CriticalIssues = [],
                HighIssues = [],
                MediumIssues = [],
                LowIssues = [],
                PositiveFindings = []
            };
        }
    }

    private static List<ReviewIssue> ParseIssues(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray().Select(e => new ReviewIssue
        {
            Line = e.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
            Issue = e.TryGetProperty("issue", out var i) ? i.GetString() ?? "" : "",
            Fix = e.TryGetProperty("fix", out var f) ? f.GetString() : null,
            Code = e.TryGetProperty("code", out var c) ? c.GetString() : null
        }).ToList();
    }

    private static List<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return [];
        return prop.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!).ToList();
    }
}
