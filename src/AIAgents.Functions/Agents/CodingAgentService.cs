using System.Net;
using System.Text.RegularExpressions;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Coding agent: uses a multi-turn agentic tool-use loop to read, understand,
/// and modify the codebase. The AI iteratively calls tools (read_file, write_file,
/// edit_file, list_files, search_code) until the implementation is complete.
/// Always transitions to Testing agent when done.
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
    private readonly IActivityLogger _activityLogger;

    public CodingAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ICodebaseContextProvider codebaseContext,
        ILogger<CodingAgentService> logger,
        IAgentTaskQueue taskQueue,
        IActivityLogger activityLogger)
    {
        _aiClientFactory = aiClientFactory;
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _codebaseContext = codebaseContext;
        _logger = logger;
        _taskQueue = taskQueue;
        _activityLogger = activityLogger;
    }

    public async Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Coding agent (agentic loop) starting for WI-{WorkItemId}", task.WorkItemId);

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

            // Get existing file structure for context
            var existingFiles = await _gitOps.ListFilesAsync(repoPath, cancellationToken);
            var fileListSummary = string.Join("\n", existingFiles.Take(100));
            if (existingFiles.Count > 100)
                fileListSummary += $"\n... and {existingFiles.Count - 100} more files";

            // Load any additional codebase context
            var codebaseCtx = await _codebaseContext.LoadRelevantContextAsync(
                repoPath, workItem.Title, workItem.Description, cancellationToken);

            // Set up the tool executor
            var toolExecutor = new CodingToolExecutor(_gitOps, repoPath, _logger);

            // Build prompts for the agentic loop
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(workItem, plan, fileListSummary, codebaseCtx);

            await _activityLogger.LogAsync("Coding", task.WorkItemId,
                "Starting agentic coding loop with tool use", "info", cancellationToken);

            // Run the agentic tool-use loop
            var agenticResult = await aiClient.CompleteWithToolsAsync(
                systemPrompt,
                userPrompt,
                CodingToolExecutor.GetToolDefinitions(),
                toolExecutor.ExecuteAsync,
                new AgenticOptions { MaxRounds = 25, MaxTokens = 8192, Temperature = 0.2 },
                cancellationToken);

            // Record token usage
            if (agenticResult.TotalUsage is not null)
                state.TokenUsage.RecordUsage("Coding", agenticResult.TotalUsage);

            var filesModified = toolExecutor.ModifiedFiles.Count;
            _logger.LogInformation(
                "Agentic loop completed for WI-{WorkItemId}: {Rounds} rounds, {ToolCalls} tool calls, {Files} files modified, completed={Completed}",
                task.WorkItemId, agenticResult.RoundsExecuted, agenticResult.ToolCalls.Count,
                filesModified, agenticResult.CompletedNaturally);

            // Track modified files as code artifacts
            foreach (var file in toolExecutor.ModifiedFiles)
                state.Artifacts.Code.Add(file);

            if (filesModified == 0)
            {
                _logger.LogWarning("Agentic loop produced no file changes for WI-{WorkItemId}", task.WorkItemId);
                await _activityLogger.LogAsync("Coding", task.WorkItemId,
                    "Warning: agentic loop produced no file changes — proceeding to testing anyway",
                    "warning", cancellationToken);
            }

            // Commit all changes
            if (filesModified > 0)
            {
                await _gitOps.CommitAndPushAsync(repoPath,
                    $"[AI Coding] US-{workItem.Id}: Implemented via agentic loop ({filesModified} file(s), {agenticResult.RoundsExecuted} rounds)",
                    cancellationToken);
            }

            // Post summary to ADO
            var toolSummary = agenticResult.ToolCalls.Count > 0
                ? string.Join("<br/>", agenticResult.ToolCalls
                    .GroupBy(t => t.ToolName)
                    .Select(g => $"• {g.Key}: {g.Count()} call(s)"))
                : "No tool calls";

            var fileSummary = filesModified > 0
                ? string.Join("<br/>", toolExecutor.ModifiedFiles.Select(f => $"• {f}"))
                : "No files modified";

            await _adoClient.AddWorkItemCommentAsync(workItem.Id,
                $"<b>🤖 AI Coding Agent Complete (Agentic Loop)</b><br/>" +
                $"Rounds: {agenticResult.RoundsExecuted} | Tool calls: {agenticResult.ToolCalls.Count} | Files: {filesModified}<br/>" +
                $"<b>Tool usage:</b><br/>{toolSummary}<br/>" +
                $"<b>Files modified:</b><br/>{fileSummary}",
                cancellationToken);

            // Update state and enqueue Testing
            state.Agents["Coding"] = AgentStatus.Completed();
            state.Agents["Coding"].AdditionalData = new Dictionary<string, object>
            {
                ["mode"] = "agentic",
                ["rounds"] = agenticResult.RoundsExecuted,
                ["toolCalls"] = agenticResult.ToolCalls.Count,
                ["filesModified"] = filesModified,
                ["completedNaturally"] = agenticResult.CompletedNaturally
            };
            state.CurrentState = "AI Test";
            await context.SaveStateAsync(state, cancellationToken);

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

            await _activityLogger.LogAsync("Coding", task.WorkItemId,
                $"Agentic loop complete: {filesModified} files modified in {agenticResult.RoundsExecuted} rounds, enqueued Testing",
                "info", cancellationToken);

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
            return AgentResult.Fail(ErrorCategory.Code, $"HTTP error in Coding agent for WI-{task.WorkItemId}: {ex.Message} [StatusCode={ex.StatusCode}]", ex);
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(ErrorCategory.Code, $"Unexpected error in Coding agent for WI-{task.WorkItemId}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Build system prompt for the agentic coding loop.
    /// </summary>
    internal static string BuildSystemPrompt() =>
        """
        You are an expert software developer implementing code changes for a user story.
        You have access to tools that let you read, write, edit, list, and search files in the repository.

        YOUR WORKFLOW:
        1. First, use list_files to understand the project structure
        2. Use read_file to examine existing code mentioned in the plan
        3. Use search_code to find relevant patterns, imports, or existing implementations
        4. Implement the changes using write_file (for new files) or edit_file (for modifying existing files)
        5. After writing/editing, use read_file to verify your changes look correct
        6. When all changes are complete, respond with a summary of what you did

        RULES:
        - Follow the implementation plan closely
        - Match existing code style, naming conventions, and patterns
        - Use edit_file for surgical changes to existing files (preferred over write_file for edits)
        - Use write_file only for creating new files or when edit_file can't express the change
        - Do NOT create or modify test files — the Testing agent handles that
        - Do NOT modify infrastructure files (Terraform, CI/CD pipelines) unless explicitly asked
        - Ensure all new code compiles and is syntactically correct
        - Add appropriate imports/usings when adding new dependencies
        - Keep changes minimal and focused on the story requirements

        When you're done, provide a brief summary of all changes made.
        """;

    /// <summary>
    /// Build user prompt with story context for the agentic loop.
    /// </summary>
    internal static string BuildUserPrompt(StoryWorkItem workItem, string plan, string fileList, string codebaseContext)
    {
        var prompt = $"""
            ## Story
            **ID:** {workItem.Id}
            **Title:** {workItem.Title}
            **Description:** {workItem.Description ?? "N/A"}

            ## Implementation Plan
            {plan}

            ## Repository File Listing
            {fileList}
            """;

        if (!string.IsNullOrWhiteSpace(codebaseContext))
            prompt += $"\n\n## Additional Codebase Context\n{codebaseContext}";

        prompt += "\n\nPlease implement the changes described in the plan. Start by reading the relevant files, then make the necessary edits.";

        return prompt;
    }

    /// <summary>
    /// Extract file paths referenced in the plan by matching against actual repository files.
    /// </summary>
    internal static List<string> ExtractReferencedFiles(string plan, IReadOnlyList<string> existingFiles)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in existingFiles)
        {
            // Check if the file path or filename appears in the plan
            var fileName = Path.GetFileName(file);
            if (plan.Contains(file, StringComparison.OrdinalIgnoreCase) ||
                (fileName.Length > 5 && plan.Contains(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                referenced.Add(file);
            }
        }

        // Also look for backtick-quoted paths like `src/foo/bar.cs`
        var backtickPaths = Regex.Matches(plan, @"`([^`]+\.[a-zA-Z]{1,10})`");
        foreach (Match match in backtickPaths)
        {
            var path = match.Groups[1].Value.Replace('\\', '/');
            var matchingFile = existingFiles.FirstOrDefault(f =>
                f.Replace('\\', '/').EndsWith(path, StringComparison.OrdinalIgnoreCase) ||
                f.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase));
            if (matchingFile is not null)
                referenced.Add(matchingFile);
        }

        return referenced.ToList();
    }
}
