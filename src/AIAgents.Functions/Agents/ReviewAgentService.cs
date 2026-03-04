using System.Net;
using System.Text.Json;
using AIAgents.Core.Constants;
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
    private readonly IAIClientFactory _aiClientFactory;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitHubApiContextService _githubContext;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ITemplateEngine _templateEngine;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger<ReviewAgentService> _logger;
    private readonly IAgentTaskQueue _taskQueue;

    public ReviewAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitHubApiContextService githubContext,
        IStoryContextFactory contextFactory,
        ITemplateEngine templateEngine,
        ICodebaseContextProvider codebaseContext,
        ILogger<ReviewAgentService> logger,
        IAgentTaskQueue taskQueue)
    {
        _aiClientFactory = aiClientFactory;
        _adoClient = adoClient;
        _githubContext = githubContext;
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
        _logger.LogInformation("Review agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
        var isInitializeCodebaseStory = workItem.Tags.Any(tag =>
            string.Equals(tag, AIPipelineNames.InitializeCodebaseTag, StringComparison.OrdinalIgnoreCase));

        if (isInitializeCodebaseStory)
        {
            _logger.LogInformation(
                "Skipping Review for InitializeCodebase story WI-{WorkItemId}; enqueuing Documentation",
                task.WorkItemId);

            try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Documentation, cancellationToken); }
            catch { /* field may not exist yet */ }

            var skipTask = new AgentTask
            {
                WorkItemId = task.WorkItemId,
                AgentType = AgentType.Documentation,
                CorrelationId = task.CorrelationId
            };
            await _taskQueue.EnqueueAsync(skipTask, cancellationToken);

            return AgentResult.Ok();
        }

        var aiClient = _aiClientFactory.GetClientForAgent("Review", workItem.GetModelOverrides());
        var branchName = $"feature/US-{task.WorkItemId}";

        await using var context = _contextFactory.Create(task.WorkItemId, string.Empty);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "AI Review";
        state.Agents["Review"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Review, cancellationToken); }
        catch { /* field may not exist yet */ }

        // Review ONLY coding artifacts — do NOT include test files or non-code artifacts.
        // This keeps the review focused and avoids sending excessive content to the AI.
        var allPaths = state.Artifacts.Code
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Reviewing {Count} files for WI-{WorkItemId}: {Files}",
            allPaths.Count, task.WorkItemId, string.Join(", ", allPaths.Take(10)));

        // Cap total code size to control cost — only send what's needed for review
        const int MaxCodeChars = 30_000; // ~7.5K tokens, keeps cost under control
        const int MaxFileChars = 10_000; // Per-file cap — truncate large files

        var fileContents = new List<string>();
        var totalChars = 0;
        var truncatedFiles = 0;

        // Fetch all code files via GitHub API
        IReadOnlyDictionary<string, string?> fetchedContents = new Dictionary<string, string?>();
        if (allPaths.Count > 0)
            fetchedContents = await _githubContext.GetFileContentsAsync(branchName, allPaths, cancellationToken);

        foreach (var path in allPaths)
        {
            if (totalChars >= MaxCodeChars)
            {
                truncatedFiles++;
                continue;
            }
            if (!fetchedContents.TryGetValue(path, out var content) || content is null) continue;

            if (content.Length > MaxFileChars)
            {
                content = content[..MaxFileChars] + $"\n\n// ... truncated ({content.Length:N0} chars total, showing first {MaxFileChars:N0})";
                _logger.LogInformation("Truncated large file {Path} ({OrigLen} chars) for Review on WI-{WorkItemId}",
                    path, content.Length, task.WorkItemId);
            }
            fileContents.Add($"// File: {path}\n{content}");
            totalChars += content.Length;
        }
        var allCode = string.Join("\n\n---\n\n", fileContents);
        if (truncatedFiles > 0)
        {
            allCode += $"\n\n// Note: {truncatedFiles} additional file(s) omitted due to size limits.";
            _logger.LogInformation("Omitted {Count} files from review due to size cap for WI-{WorkItemId}",
                truncatedFiles, task.WorkItemId);
        }

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

        // Skip codebase context entirely for review — the review agent only needs
        // the actual code being reviewed, not broader codebase documentation.
        // This prevents sending excessive context to the AI and controls costs.
        var codebaseCtx = "";

        var userPrompt = $@"## Story
**ID:** {workItem.Id}
**Title:** {workItem.Title}

## Code to Review
{allCode}

{codebaseCtx}

Perform a comprehensive code review.";

        var aiResult = await aiClient.CompleteAsync(systemPrompt, userPrompt,
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

        // Commit review artifact via GitHub API
        await _githubContext.WriteFilesAsync(
            branchName,
            new Dictionary<string, string>
            {
                [$".ado/stories/US-{workItem.Id}/CODE_REVIEW.md"] = renderedReview
            },
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
            ["recommendation"] = reviewResult.Recommendation,
            ["criticalIssues"] = reviewResult.CriticalIssues.Count
        };
        state.CurrentState = "AI Docs";
        await context.SaveStateAsync(state, cancellationToken);

        // Track last agent in ADO
        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.LastAgent, "Review", cancellationToken); }
        catch { /* field may not exist yet */ }
        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Documentation, cancellationToken); }
        catch { /* field may not exist yet */ }

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Documentation,
            CorrelationId = task.CorrelationId
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        _logger.LogInformation("Review agent completed for WI-{WorkItemId}, enqueued Documentation agent", task.WorkItemId);

            return AgentResult.Ok(aiResult.Usage?.TotalTokens ?? 0, aiResult.Usage?.EstimatedCost ?? 0m);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"Rate limit hit for Review agent on WI-{task.WorkItemId}", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AgentResult.Fail(ErrorCategory.Configuration, $"Authentication failed for Review agent on WI-{task.WorkItemId}. Check API key.", ex);
        }
        catch (HttpRequestException ex)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"HTTP error in Review agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(ErrorCategory.Code, $"Unexpected error in Review agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
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
        catch (Exception)
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

        return arr.EnumerateArray().Select(e =>
        {
            // Handle AI returning plain strings instead of objects
            if (e.ValueKind == JsonValueKind.String)
                return new ReviewIssue { Issue = e.GetString() ?? "" };
            if (e.ValueKind != JsonValueKind.Object)
                return null;
            return new ReviewIssue
            {
                Line = e.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
                Issue = e.TryGetProperty("issue", out var i) ? i.GetString() ?? "" : "",
                Fix = e.TryGetProperty("fix", out var f) ? f.GetString() : null,
                Code = e.TryGetProperty("code", out var c) ? c.GetString() : null
            };
        }).Where(r => r is not null).ToList()!;
    }

    private static List<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return [];
        return prop.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!).ToList();
    }
}
