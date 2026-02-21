using System.Net;
using System.Text.RegularExpressions;
using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Coding agent orchestrator: routes coding work to the appropriate strategy based on
/// story complexity and configuration. Supports two strategies:
/// <list type="bullet">
///   <item><see cref="AgenticCodingStrategy"/> — built-in multi-turn agentic tool-use loop (default)</item>
///   <item><see cref="CopilotCodingStrategy"/> — delegates to GitHub Copilot's coding agent</item>
/// </list>
/// 
/// Strategy selection (in priority order):
/// 1. Force-agentic flag (set by Copilot timeout fallback) → always agentic
/// 2. ADO field <c>Custom.AICodingProvider</c> → "Agentic" or "Copilot" override per story
/// 3. Global <c>Copilot:Mode</c> → "Always" sends everything to Copilot
/// 4. Threshold: stories ≥ <c>Copilot:ComplexityThreshold</c> SP → Copilot, else agentic
/// 5. When Copilot is disabled in config, always uses the agentic strategy
/// 
/// For the agentic path, this class handles the full lifecycle (commit, ADO update, enqueue Testing).
/// For the Copilot path, the pipeline pauses — <see cref="Functions.CopilotBridgeWebhook"/> handles resumption.
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
    private readonly CopilotOptions _copilotOptions;
    private readonly ICopilotDelegationService _delegationService;
    private readonly IOptions<GitHubOptions> _githubOptions;
    private readonly IOptions<CopilotOptions> _copilotOptionsAccessor;

    public CodingAgentService(
        IAIClientFactory aiClientFactory,
        IAzureDevOpsClient adoClient,
        IGitOperations gitOps,
        IStoryContextFactory contextFactory,
        ICodebaseContextProvider codebaseContext,
        ILogger<CodingAgentService> logger,
        IAgentTaskQueue taskQueue,
        IActivityLogger activityLogger,
        IOptions<CopilotOptions> copilotOptions,
        ICopilotDelegationService delegationService,
        IOptions<GitHubOptions> githubOptions)
    {
        _aiClientFactory = aiClientFactory;
        _adoClient = adoClient;
        _gitOps = gitOps;
        _contextFactory = contextFactory;
        _codebaseContext = codebaseContext;
        _logger = logger;
        _taskQueue = taskQueue;
        _activityLogger = activityLogger;
        _copilotOptions = copilotOptions.Value;
        _copilotOptionsAccessor = copilotOptions;
        _delegationService = delegationService;
        _githubOptions = githubOptions;
    }

    public async Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Coding agent starting for WI-{WorkItemId}", task.WorkItemId);

            var workItem = await _adoClient.GetWorkItemAsync(task.WorkItemId, cancellationToken);
            var branchName = $"feature/US-{task.WorkItemId}";
            var repoPath = await _gitOps.EnsureBranchAsync(branchName, cancellationToken);

            // Materialize work-item supporting files so coding agents can inspect local documents and visuals
            var supportingArtifacts = await _adoClient.DownloadSupportingArtifactsAsync(task.WorkItemId, repoPath, cancellationToken);

            await using var context = _contextFactory.Create(task.WorkItemId, repoPath);
            var state = await context.LoadStateAsync(cancellationToken);
            state.CurrentState = "AI Code";
            state.Agents["Coding"] = AgentStatus.InProgress();
            await context.SaveStateAsync(state, cancellationToken);

            try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Coding, cancellationToken); }
            catch { /* field may not exist yet */ }

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

            // Build the coding context shared by all strategies
            var codingContext = new CodingContext
            {
                WorkItemId = task.WorkItemId,
                RepositoryPath = repoPath,
                State = state,
                WorkItem = workItem,
                PlanMarkdown = plan,
                CodingGuidelines = codebaseCtx,
                ExistingFilesSummary = fileListSummary,
                StoryDocumentsFolder = supportingArtifacts.StoryDocumentsFolder,
                AttachedImagePaths = supportingArtifacts.ImagePaths,
                AttachedDocumentPaths = supportingArtifacts.DocumentPaths,
                BranchName = branchName,
                CorrelationId = task.CorrelationId
            };

            // Resolve the coding strategy
            var strategy = ResolveStrategy(state, workItem);
            var strategyName = strategy is CopilotCodingStrategy copilotStrategy
                ? $"GitHub @{copilotStrategy.AgentAssignee}"
                : "Agentic";

            await _activityLogger.LogAsync("Coding", task.WorkItemId,
                $"Starting coding ({strategyName} strategy)", "info", cancellationToken);

            // Execute the coding strategy
            var result = await strategy.ExecuteAsync(codingContext, cancellationToken);

            if (result.Mode == "copilot-delegated")
            {
                // ── Copilot path: pipeline pauses, bridge will resume ──
                await context.SaveStateAsync(state, cancellationToken);

                var agentName = (strategy as CopilotCodingStrategy)?.AgentAssignee ?? "copilot";

                // Commit state.json so it's visible on the branch
                await _gitOps.CommitAndPushAsync(repoPath,
                    $"[AI Coding] US-{workItem.Id}: Delegated to @{agentName} (Issue #{result.CopilotMetrics?.IssueNumber})",
                    cancellationToken);

                await _adoClient.AddWorkItemCommentAsync(workItem.Id,
                    $"<b>🤖 AI Coding Agent — Delegated to GitHub @{agentName}</b><br/>" +
                    $"Strategy: GitHub {agentName} coding agent<br/>" +
                    (result.CopilotMetrics?.IssueNumber > 0
                        ? $"GitHub Issue: <a href=\"https://github.com/{_githubOptions.Value.Owner}/{_githubOptions.Value.Repo}/issues/{result.CopilotMetrics.IssueNumber}\">#{result.CopilotMetrics.IssueNumber}</a><br/>"
                        : "") +
                    $"Pipeline is paused — waiting for @{agentName} to create a PR.<br/>" +
                    $"Timeout: {_copilotOptions.TimeoutMinutes}m (auto-fallback to agentic loop)",
                    cancellationToken);

                try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.LastAgent, "Coding", cancellationToken); }
                catch { /* field may not exist yet */ }

                try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, string.Empty, cancellationToken); }
                catch { /* field may not exist yet */ }

                await _activityLogger.LogAsync("Coding", task.WorkItemId,
                    $"Delegated to @{agentName} (Issue #{result.CopilotMetrics?.IssueNumber}). Pipeline paused.",
                    "info", cancellationToken);

                _logger.LogInformation(
                    "Coding agent delegated WI-{WorkItemId} to @{Agent}. Pipeline paused until bridge webhook resumes.",
                    task.WorkItemId, agentName);

                return AgentResult.Ok(0, 0m);
            }

            // ── Agentic path: full lifecycle ──
            // Save state with artifacts BEFORE committing
            await context.SaveStateAsync(state, cancellationToken);

            var filesModified = result.ModifiedFiles.Count;

            if (filesModified == 0)
            {
                _logger.LogWarning("Agentic loop produced no file changes for WI-{WorkItemId}", task.WorkItemId);

                await _activityLogger.LogAsync("Coding", task.WorkItemId,
                    $"Warning: agentic loop produced no file changes ({result.AgenticMetrics?.Rounds} rounds, {result.AgenticMetrics?.ToolCalls} tool calls) — proceeding to testing anyway",
                    "warning", cancellationToken);
            }

            // Commit all changes
            if (filesModified > 0)
            {
                await _gitOps.CommitAndPushAsync(repoPath,
                    $"[AI Coding] US-{workItem.Id}: Implemented via agentic loop ({filesModified} file(s), {result.AgenticMetrics?.Rounds} rounds)",
                    cancellationToken);
            }

            // Post summary to ADO
            var fileSummary = filesModified > 0
                ? string.Join("<br/>", result.ModifiedFiles.Select(f => $"• {f}"))
                : "No files modified";

            await _adoClient.AddWorkItemCommentAsync(workItem.Id,
                $"<b>🤖 AI Coding Agent Complete (Agentic Loop)</b><br/>" +
                $"Rounds: {result.AgenticMetrics?.Rounds} | Tool calls: {result.AgenticMetrics?.ToolCalls} | Files: {filesModified}<br/>" +
                $"<b>Files modified:</b><br/>{fileSummary}",
                cancellationToken);

            // Update state and enqueue Testing
            state.Agents["Coding"] = AgentStatus.Completed();
            state.Agents["Coding"].AdditionalData = new Dictionary<string, object>
            {
                ["mode"] = "agentic",
                ["rounds"] = result.AgenticMetrics?.Rounds ?? 0,
                ["toolCalls"] = result.AgenticMetrics?.ToolCalls ?? 0,
                ["filesModified"] = filesModified,
                ["completedNaturally"] = result.AgenticMetrics?.CompletedNaturally ?? false
            };
            state.CurrentState = "AI Test";
            await context.SaveStateAsync(state, cancellationToken);

            try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.LastAgent, "Coding", cancellationToken); }
            catch { /* field may not exist yet */ }
            try { await _adoClient.UpdateWorkItemFieldAsync(workItem.Id, CustomFieldNames.Paths.CurrentAIAgent, AIPipelineNames.CurrentAgentValues.Testing, cancellationToken); }
            catch { /* field may not exist yet */ }

            var nextTask = new AgentTask
            {
                WorkItemId = task.WorkItemId,
                AgentType = AgentType.Testing,
                CorrelationId = task.CorrelationId
            };
            await _taskQueue.EnqueueAsync(nextTask, cancellationToken);

            await _activityLogger.LogAsync("Coding", task.WorkItemId,
                $"Agentic loop complete: {filesModified} files modified in {result.AgenticMetrics?.Rounds} rounds, enqueued Testing",
                "info", cancellationToken);

            _logger.LogInformation("Coding agent completed for WI-{WorkItemId}, enqueued Testing agent", task.WorkItemId);

            return AgentResult.Ok(result.Tokens, result.Cost);
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
    /// Resolves which coding strategy to use based on configuration, mode, complexity, and overrides.
    /// Priority: force-agentic flag → per-story AICodingProvider → Copilot disabled → Mode=Always → Mode=Auto (threshold) → agentic.
    /// </summary>
    internal ICodingStrategy ResolveStrategy(StoryState state, StoryWorkItem workItem)
    {
        // Check for force-agentic flag (set by timeout fallback)
        if (state.Agents.TryGetValue("Coding", out var codingStatus) &&
            codingStatus.AdditionalData?.TryGetValue("forceAgentic", out var force) == true &&
            force is true or "true")
        {
            _logger.LogInformation("Force-agentic flag set for WI-{WorkItemId} (timeout fallback)", workItem.Id);
            return CreateAgenticStrategy();
        }

        // Per-story ADO field override: "Agentic" or "Copilot" short-circuits all config
        if (!string.IsNullOrWhiteSpace(workItem.AICodingProvider) &&
            !string.Equals(workItem.AICodingProvider, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(workItem.AICodingProvider, "Agentic", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Routing WI-{WorkItemId} to agentic strategy (per-story AICodingProvider override)",
                    workItem.Id);
                return CreateAgenticStrategy();
            }

            // Copilot / Claude / Codex all delegate to a GitHub agent
            var agentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Copilot"] = "copilot",
                ["Claude"] = "claude",
                ["Codex"] = "codex"
            };

            if (agentMap.TryGetValue(workItem.AICodingProvider!, out var agentName))
            {
                if (!_copilotOptions.Enabled)
                {
                    _logger.LogWarning(
                        "WI-{WorkItemId} has AICodingProvider={Provider} but Copilot is disabled in config. Falling back to agentic.",
                        workItem.Id, workItem.AICodingProvider);
                    return CreateAgenticStrategy();
                }

                _logger.LogInformation(
                    "Routing WI-{WorkItemId} to {Agent} strategy (per-story AICodingProvider override)",
                    workItem.Id, agentName);
                return CreateCopilotStrategy(agentName);
            }

            _logger.LogWarning(
                "WI-{WorkItemId} has unknown AICodingProvider value '{Provider}'. Ignoring override.",
                workItem.Id, workItem.AICodingProvider);
        }

        if (!_copilotOptions.Enabled)
        {
            return CreateAgenticStrategy();
        }

        // Mode = Always → every story goes to Copilot, no threshold check
        if (string.Equals(_copilotOptions.Mode, "Always", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Routing WI-{WorkItemId} to Copilot strategy (Mode=Always)", workItem.Id);

            return CreateCopilotStrategy();
        }

        // Mode = Auto (default) → route based on complexity threshold
        var storyPoints = GetStoryPointsFromDecisions(state);

        if (storyPoints >= _copilotOptions.ComplexityThreshold)
        {
            _logger.LogInformation(
                "Routing WI-{WorkItemId} to Copilot strategy ({StoryPoints} SP ≥ {Threshold} threshold)",
                workItem.Id, storyPoints, _copilotOptions.ComplexityThreshold);

            return CreateCopilotStrategy();
        }

        _logger.LogInformation(
            "Routing WI-{WorkItemId} to agentic strategy ({StoryPoints} SP < {Threshold} threshold)",
            workItem.Id, storyPoints, _copilotOptions.ComplexityThreshold);

        return CreateAgenticStrategy();
    }

    private AgenticCodingStrategy CreateAgenticStrategy() =>
        new(_aiClientFactory, _gitOps, _codebaseContext, _logger);

    private CopilotCodingStrategy CreateCopilotStrategy(string? agentOverride = null) =>
        new(_githubOptions, _copilotOptionsAccessor, _delegationService, _logger, agentOverride);

    /// <summary>
    /// Extracts story points from the Planning agent's complexity decision.
    /// Returns 0 if no complexity info is available.
    /// </summary>
    internal static int GetStoryPointsFromDecisions(StoryState state)
    {
        var planningDecision = state.Decisions
            .FirstOrDefault(d => d.Agent == "Planning" &&
                                 d.DecisionText.Contains("complexity", StringComparison.OrdinalIgnoreCase));

        if (planningDecision is not null)
        {
            var match = Regex.Match(planningDecision.DecisionText, @"(\d+)\s*story\s*points?", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var points))
                return points;
        }

        return 0;
    }

    /// <summary>
    /// Build system prompt for the agentic coding loop.
    /// Optimized for efficiency — direct instructions to minimize unnecessary exploration.
    /// </summary>
    internal static string BuildSystemPrompt() =>
        """
        You are an expert developer implementing code changes. Use ONLY the provided tools.

        WORKFLOW (be efficient — minimize unnecessary calls):
        1. list_files to see the project structure
        2. read_file to examine the specific files you need to change
        3. edit_file or write_file to make changes
        4. Respond with a brief summary when done

        EDIT_FILE RULES:
        - read_file shows "  42| code" — use the actual text WITHOUT line numbers in edit_file search
        - Include 3-5 lines of surrounding context in search for unique matching
        - Line endings are normalized automatically
        - If search fails, re-read the file and use exact text

        RULES:
        - Focus on the story requirements — do not refactor unrelated code
        - Match existing code style
        - Use edit_file for modifications (preferred), write_file only for new files
        - Do NOT modify test files or infrastructure (Terraform/CI)
        - Ensure correct syntax, imports, and compilation
        - Be direct: read what you need, make the edit, move on

        Start by calling list_files.
        """;

    /// <summary>
    /// Build user prompt with story context for the agentic loop.
    /// </summary>
    internal static string BuildUserPrompt(
        StoryWorkItem workItem,
        string plan,
        string fileList,
        string codebaseContext,
        string? storyDocumentsFolder = null,
        IReadOnlyList<string>? attachedImagePaths = null,
        IReadOnlyList<string>? attachedDocumentPaths = null)
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

        if (!string.IsNullOrWhiteSpace(storyDocumentsFolder))
        {
            prompt += $"\n\n## Story Supporting Files Folder\nAll supporting files from ADO (attachments and pasted visuals) are available in this repository folder:\n- {storyDocumentsFolder}\nReview this folder before implementing.";
        }

        if (attachedImagePaths is not null && attachedImagePaths.Count > 0)
        {
            prompt += $"\n\n## Attached Visual References\n" +
                      "The story includes image attachments materialized in this repository. " +
                      "Inspect these files before editing UI/layout code:\n" +
                      string.Join("\n", attachedImagePaths.Select(path => $"- {path}"));
        }

        if (attachedDocumentPaths is not null && attachedDocumentPaths.Count > 0)
        {
            prompt += $"\n\n## Attached Document References\n" +
                      "The story includes supporting documents. Read these files for requirements and implementation details:\n" +
                      string.Join("\n", attachedDocumentPaths.Select(path => $"- {path}"));
        }

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

    /// <summary>
    /// Determines MaxRounds for the agentic loop based on the Planning agent's
    /// complexity assessment. Reduces cost for simple stories by limiting rounds.
    /// </summary>
    internal static int GetMaxRoundsForComplexity(StoryState state)
    {
        var points = GetStoryPointsFromDecisions(state);

        if (points > 0)
        {
            return points switch
            {
                <= 2 => 10,  // trivial: 10 rounds max (~$0.40-0.70)
                <= 5 => 15,  // moderate: 15 rounds (~$0.70-1.00)
                <= 8 => 20,  // complex: 20 rounds (~$1.00-1.50)
                _ => 25       // very complex: full 25 rounds
            };
        }

        // Default: moderate rounds if no complexity info available
        return 15;
    }
}
