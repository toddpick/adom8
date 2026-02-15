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
/// Coding agent: generates source code based on the planning analysis.
/// Reads the plan, generates code files, commits to the feature branch.
/// Transitions: AI Code → AI Test (enqueues Testing agent).
/// </summary>
public sealed class CodingAgentService : IAgentService
{
    private readonly IAIClientFactory _aiClientFactory;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger<CodingAgentService> _logger;
    private readonly IAgentTaskQueue _taskQueue;

    public CodingAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ICodebaseContextProvider codebaseContext,
        ILogger<CodingAgentService> logger,
        IAgentTaskQueue taskQueue)
    {
        _aiClientFactory = aiClientFactory;
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _codebaseContext = codebaseContext;
        _logger = logger;
        _taskQueue = taskQueue;
    }

    public async Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        try
        {
        _logger.LogInformation("Coding agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
        var aiClient = _aiClientFactory.GetClientForAgent("Coding", workItem.GetModelOverrides());
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "AI Code";
        state.Agents["Coding"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        // Read the plan
        var plan = await context.ReadArtifactAsync("PLAN.md", cancellationToken)
            ?? "No plan found. Generate code based on the story description.";

        // Get existing file structure
        var existingFiles = await _gitOps.ListFilesAsync(repoPath, cancellationToken);
        var fileListSummary = string.Join("\n", existingFiles.Take(100));

        // Call AI for code generation
        var systemPrompt = @"You are a senior software developer generating production-quality code.
Based on the provided plan and story, generate the necessary code files.
Respond ONLY with valid JSON as an array of file objects:
[
  {
    ""relativePath"": ""src/path/to/file.cs"",
    ""content"": ""full file content"",
    ""isNew"": true
  }
]
Follow these guidelines:
- Use idiomatic patterns for the language
- Include proper error handling
- Add XML documentation comments
- Follow SOLID principles
- Do NOT generate test files (the testing agent handles that)";

        var userPrompt = $@"## Story
**ID:** {workItem.Id}
**Title:** {workItem.Title}
**Description:** {workItem.Description ?? "N/A"}

## Implementation Plan
{plan}

## Existing Files
{fileListSummary}

{await _codebaseContext.LoadRelevantContextAsync(repoPath, workItem.Title, workItem.Description, cancellationToken)}

Generate all necessary code files for this story.";

        var aiResult = await aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 8192, Temperature = 0.2 }, cancellationToken);
        state.TokenUsage.RecordUsage("Coding", aiResult.Usage);

        // Parse and write code files
        var codeFiles = ParseCodeFiles(aiResult.Content);

        foreach (var file in codeFiles)
        {
            await _gitOps.WriteFileAsync(repoPath, file.RelativePath, file.Content, cancellationToken);
            state.Artifacts.Code.Add(file.RelativePath);
            _logger.LogInformation("Generated: {FilePath}", file.RelativePath);
        }

        // Commit
        await _gitOps.CommitAndPushAsync(repoPath,
            $"[AI Coding] US-{workItem.Id}: Generated {codeFiles.Count} file(s)", cancellationToken);

        // Update ADO
        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>🤖 AI Coding Agent Complete</b><br/>Generated {codeFiles.Count} file(s):<br/>" +
            string.Join("<br/>", codeFiles.Select(f => $"• {f.RelativePath}")),
            cancellationToken);

        // Update state and enqueue next
        state.Agents["Coding"] = AgentStatus.Completed();
        state.Agents["Coding"].AdditionalData = new Dictionary<string, object>
        {
            ["filesGenerated"] = codeFiles.Count
        };
        state.CurrentState = "AI Test";
        await context.SaveStateAsync(state, cancellationToken);

        // Track last agent in ADO
        try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.LastAgent, "Coding", cancellationToken); }
        catch { /* field may not exist yet */ }

        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "AI Test", cancellationToken);

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Testing,
            CorrelationId = task.CorrelationId
        };
        await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

        _logger.LogInformation("Coding agent completed for WI-{WorkItemId}, enqueued Testing agent", task.WorkItemId);

            return AgentResult.Ok();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"Rate limit hit for Coding agent on WI-{task.WorkItemId}", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return AgentResult.Fail(ErrorCategory.Configuration, $"Authentication failed for Coding agent on WI-{task.WorkItemId}. Check API key.", ex);
        }
        catch (HttpRequestException ex)
        {
            return AgentResult.Fail(ErrorCategory.Transient, $"HTTP error in Coding agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(ErrorCategory.Code, $"Unexpected error in Coding agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
    }

    private static List<CodeFile> ParseCodeFiles(string aiResponse)
    {
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
            var files = JsonSerializer.Deserialize<List<CodeFileDto>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return files?.Select(f => new CodeFile
            {
                RelativePath = f.RelativePath ?? "unknown.cs",
                Content = f.Content ?? "",
                IsNew = f.IsNew
            }).ToList() ?? [];
        }
        catch (JsonException)
        {
            // If JSON parsing fails, save the raw response as a single file
            return [new CodeFile
            {
                RelativePath = "ai-generated-code.txt",
                Content = aiResponse,
                IsNew = true
            }];
        }
    }

    private sealed class CodeFileDto
    {
        public string? RelativePath { get; set; }
        public string? Content { get; set; }
        public bool IsNew { get; set; } = true;
    }
}
