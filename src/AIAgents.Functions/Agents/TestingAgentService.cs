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
/// Testing agent: generates test cases and test code based on the implementation.
/// Reads generated code files, creates unit/integration tests.
/// Transitions: AI Test → AI Review (enqueues Review agent).
/// </summary>
public sealed class TestingAgentService : IAgentService
{
    private readonly IAIClient _aiClient;
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IGitOperations _gitOps;
    private readonly IStoryContextFactory _contextFactory;
    private readonly ITemplateEngine _templateEngine;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger<TestingAgentService> _logger;
    private readonly string _storageConnectionString;

    public TestingAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ITemplateEngine templateEngine,
        ICodebaseContextProvider codebaseContext,
        ILogger<TestingAgentService> logger,
        IConfiguration configuration)
    {
        _aiClient = aiClientFactory.GetClientForAgent("Testing");
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
        _logger.LogInformation("Testing agent starting for WI-{WorkItemId}", task.WorkItemId);

        var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
        var branchName = $"feature/US-{task.WorkItemId}";
        var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

        await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
        var state = await context.LoadStateAsync(cancellationToken);
        state.CurrentState = "AI Test";
        state.Agents["Testing"] = AgentStatus.InProgress();
        await context.SaveStateAsync(state, cancellationToken);

        // Read the plan and generated code
        var plan = await context.ReadArtifactAsync("PLAN.md", cancellationToken) ?? "";

        // Read all generated code files content
        var codeContent = new List<string>();
        foreach (var codePath in state.Artifacts.Code)
        {
            var content = await _gitOps.ReadFileAsync(repoPath, codePath, cancellationToken);
            if (content is not null)
            {
                codeContent.Add($"// File: {codePath}\n{content}");
            }
        }

        var allCode = string.Join("\n\n---\n\n", codeContent);

        // Call AI for test generation
        var systemPrompt = @"You are a senior test engineer creating comprehensive tests.
Based on the provided code and plan, generate test files and a test plan.
Respond ONLY with valid JSON:
{
  ""testCases"": [
    {
      ""name"": ""string"",
      ""type"": ""Unit|Integration|E2E"",
      ""priority"": ""High|Medium|Low"",
      ""description"": ""string"",
      ""expectedResult"": ""string""
    }
  ],
  ""testFiles"": [
    {
      ""relativePath"": ""tests/path/to/TestFile.cs"",
      ""content"": ""full test file content"",
      ""isNew"": true
    }
  ]
}
Use xUnit and Moq for .NET test projects. Include edge cases and error scenarios.";

        var userPrompt = $@"## Story
**ID:** {workItem.Id}
**Title:** {workItem.Title}
**Acceptance Criteria:** {workItem.AcceptanceCriteria ?? "N/A"}

## Implementation Plan
{plan}

## Generated Code
{allCode}

{await _codebaseContext.LoadRelevantContextAsync(repoPath, workItem.Title, workItem.Description, cancellationToken)}

Generate comprehensive tests for this implementation.";

        var aiResponse = await _aiClient.CompleteAsync(systemPrompt, userPrompt,
            new AICompletionOptions { MaxTokens = 8192, Temperature = 0.2 }, cancellationToken);

        // Parse response
        var (testCases, testFiles) = ParseTestResponse(aiResponse);

        // Write test files
        foreach (var file in testFiles)
        {
            await _gitOps.WriteFileAsync(repoPath, file.RelativePath, file.Content, cancellationToken);
            state.Artifacts.Tests.Add(file.RelativePath);
            _logger.LogInformation("Generated test: {FilePath}", file.RelativePath);
        }

        // Render test plan template
        var templateModel = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = $"US-{workItem.Id}",
            ["TITLE"] = workItem.Title,
            ["TEST_CASES"] = testCases.Select(tc => new Dictionary<string, object?>
            {
                ["NAME"] = tc.Name,
                ["TYPE"] = tc.Type,
                ["PRIORITY"] = tc.Priority,
                ["DESCRIPTION"] = tc.Description,
                ["EXPECTED_RESULT"] = tc.ExpectedResult
            }).ToList(),
            ["TIMESTAMP"] = DateTime.UtcNow.ToString("O")
        };

        var renderedTestPlan = await _templateEngine.RenderAsync("TEST_PLAN.template.md", templateModel, cancellationToken);
        await context.WriteArtifactAsync("TEST_PLAN.md", renderedTestPlan, cancellationToken);

        // Commit
        await _gitOps.CommitAndPushAsync(repoPath,
            $"[AI Testing] US-{workItem.Id}: Generated {testFiles.Count} test file(s), {testCases.Count} test cases",
            cancellationToken);

        // Update ADO
        await _adoClient.AddWorkItemCommentAsync(workItem.Id,
            $"<b>🤖 AI Testing Agent Complete</b><br/>Test cases: {testCases.Count}<br/>Test files: {testFiles.Count}",
            cancellationToken);

        // Update state and enqueue next
        state.Agents["Testing"] = AgentStatus.Completed();
        state.CurrentState = "AI Review";
        await context.SaveStateAsync(state, cancellationToken);

        await _adoClient.UpdateWorkItemStateAsync(workItem.Id, "AI Review", cancellationToken);

        var nextTask = new AgentTask
        {
            WorkItemId = task.WorkItemId,
            AgentType = AgentType.Review,
            CorrelationId = task.CorrelationId
        };
        var queueClient = new QueueClient(_storageConnectionString, "agent-tasks");
        var messageJson = JsonSerializer.Serialize(nextTask);
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
        await queueClient.SendMessageAsync(base64, cancellationToken);

        _logger.LogInformation("Testing agent completed for WI-{WorkItemId}, enqueued Review agent", task.WorkItemId);
    }

    private static (List<TestCase> testCases, List<CodeFile> testFiles) ParseTestResponse(string aiResponse)
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

            var testCases = new List<TestCase>();
            if (root.TryGetProperty("testCases", out var tcArray))
            {
                foreach (var tc in tcArray.EnumerateArray())
                {
                    testCases.Add(new TestCase
                    {
                        Name = tc.GetProperty("name").GetString() ?? "Unnamed",
                        Type = tc.GetProperty("type").GetString() ?? "Unit",
                        Priority = tc.GetProperty("priority").GetString() ?? "Medium",
                        Description = tc.GetProperty("description").GetString() ?? "",
                        ExpectedResult = tc.GetProperty("expectedResult").GetString() ?? ""
                    });
                }
            }

            var testFiles = new List<CodeFile>();
            if (root.TryGetProperty("testFiles", out var tfArray))
            {
                foreach (var tf in tfArray.EnumerateArray())
                {
                    testFiles.Add(new CodeFile
                    {
                        RelativePath = tf.GetProperty("relativePath").GetString() ?? "tests/Test.cs",
                        Content = tf.GetProperty("content").GetString() ?? "",
                        IsNew = tf.TryGetProperty("isNew", out var isNew) && isNew.GetBoolean()
                    });
                }
            }

            return (testCases, testFiles);
        }
        catch (JsonException)
        {
            return ([], [new CodeFile
            {
                RelativePath = "tests/ai-generated-tests.txt",
                Content = aiResponse,
                IsNew = true
            }]);
        }
    }
}
