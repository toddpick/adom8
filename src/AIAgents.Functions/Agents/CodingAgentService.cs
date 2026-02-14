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
/// Coding agent: generates source code based on the planning analysis.
/// Reads the plan, generates code files, commits to the feature branch.
/// Transitions: AI Code → AI Test (enqueues Testing agent).
/// </summary>
public sealed class CodingAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ILogger<CodingAgentService> _logger;
    private readonly string _storageConnectionString;

    public CodingAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ILogger<CodingAgentService> logger,
        IConfiguration configuration)
    {
        _aiClient = aiClientFactory.GetClientForAgent("Coding");
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _logger = logger;
        _storageConnectionString = configuration["AzureWebJobsStorage"]!;
    }

    public async Task ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Coding agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
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

Generate all necessary code files for this story.";

        var aiResponse = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 8192, Temperature = 0.2 }, cancellationToken);

        // Parse and write code files
        var codeFiles = ParseCodeFiles(aiResponse);

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
        state.CurrentState = "AI Test";
        await context.SaveStateAsync(state, cancellationToken);

        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "AI Test", cancellationToken);

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Testing,
            CorrelationId = task.CorrelationId
        };
        var queueClient = new QueueClient(_storageConnectionString, "agent-tasks");
        var messageJson = JsonSerializer.Serialize(nextTask);
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
        await queueClient.SendMessageAsync(base64, cancellationToken);

        _logger.LogInformation("Coding agent completed for WI-{WorkItemId}, enqueued Testing agent", task.WorkItemId);
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
