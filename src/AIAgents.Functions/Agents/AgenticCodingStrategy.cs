using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Built-in coding strategy: runs a multi-turn agentic tool-use loop where the AI
/// iteratively reads, understands, and modifies the codebase using tools
/// (read_file, write_file, edit_file, list_files, search_code).
/// All file writes are buffered in memory and committed atomically via GitHub Trees API
/// at the end — no local git clone needed.
///
/// This is the default strategy and handles the majority of stories. Extracted from
/// <see cref="CodingAgentService"/> to support the Strategy pattern for hybrid
/// Copilot integration.
/// </summary>
public sealed class AgenticCodingStrategy : ICodingStrategy
{
    private readonly IAIClientFactory _aiClientFactory;
    private readonly IGitHubApiContextService _githubContext;
    private readonly ICodebaseContextProvider _codebaseContext;
    private readonly ILogger _logger;

    public AgenticCodingStrategy(
        IAIClientFactory aiClientFactory,
        IGitHubApiContextService githubContext,
        ICodebaseContextProvider codebaseContext,
        ILogger logger)
    {
        _aiClientFactory = aiClientFactory;
        _githubContext = githubContext;
        _codebaseContext = codebaseContext;
        _logger = logger;
    }

    public async Task<CodingResult> ExecuteAsync(CodingContext context, CancellationToken cancellationToken = default)
    {
        var aiClient = _aiClientFactory.GetClientForAgent("Coding", context.WorkItem.GetModelOverrides());

        // Set up the tool executor — uses GitHub API, buffers writes in memory
        var toolExecutor = new CodingToolExecutor(_githubContext, context.BranchName, _logger);

        // Build prompts for the agentic loop
        var systemPrompt = CodingAgentService.BuildSystemPrompt();
        var userPrompt = CodingAgentService.BuildUserPrompt(
            context.WorkItem,
            context.PlanMarkdown,
            context.ExistingFilesSummary,
            context.CodingGuidelines,
            context.StoryDocumentsFolder,
            context.AttachedImagePaths,
            context.AttachedDocumentPaths);

        // Scale MaxRounds by complexity from the planning decision
        var maxRounds = CodingAgentService.GetMaxRoundsForComplexity(context.State);

        _logger.LogInformation(
            "Starting agentic coding loop for WI-{WorkItemId} (maxRounds={MaxRounds})",
            context.WorkItemId, maxRounds);

        // Run the agentic tool-use loop
        var agenticResult = await aiClient.CompleteWithToolsAsync(
            systemPrompt,
            userPrompt,
            CodingToolExecutor.GetToolDefinitions(),
            toolExecutor.ExecuteAsync,
            new AgenticOptions { MaxRounds = maxRounds, MaxTokens = 8192, Temperature = 0.2 },
            cancellationToken);

        // Record token usage
        if (agenticResult.TotalUsage is not null)
            context.State.TokenUsage.RecordUsage("Coding", agenticResult.TotalUsage);

        var filesModified = toolExecutor.ModifiedFiles.Count;
        _logger.LogInformation(
            "Agentic loop completed for WI-{WorkItemId}: {Rounds} rounds, {ToolCalls} tool calls, {Files} files modified, completed={Completed}",
            context.WorkItemId, agenticResult.RoundsExecuted, agenticResult.ToolCalls.Count,
            filesModified, agenticResult.CompletedNaturally);

        // Commit all buffered file writes atomically via GitHub Trees API
        if (toolExecutor.PendingWrites.Count > 0)
        {
            await _githubContext.WriteFilesAsync(
                context.BranchName,
                toolExecutor.PendingWrites,
                $"[AI Coding] US-{context.WorkItemId}: Implemented via agentic loop ({filesModified} file(s), {agenticResult.RoundsExecuted} rounds)",
                cancellationToken);

            _logger.LogInformation(
                "Committed {Count} files to branch {Branch} for WI-{WorkItemId}",
                toolExecutor.PendingWrites.Count, context.BranchName, context.WorkItemId);
        }

        // Track modified files as code artifacts
        foreach (var file in toolExecutor.ModifiedFiles)
            context.State.Artifacts.Code.Add(file);

        return new CodingResult
        {
            Success = true,
            Mode = "agentic",
            ModifiedFiles = toolExecutor.ModifiedFiles.ToList(),
            Tokens = agenticResult.TotalUsage?.TotalTokens ?? 0,
            Cost = agenticResult.TotalUsage?.EstimatedCost ?? 0m,
            Summary = agenticResult.FinalResponse ?? $"Agentic loop completed: {filesModified} files modified",
            AgenticMetrics = new AgenticMetrics
            {
                Rounds = agenticResult.RoundsExecuted,
                ToolCalls = agenticResult.ToolCalls.Count,
                CompletedNaturally = agenticResult.CompletedNaturally
            }
        };
    }
}
