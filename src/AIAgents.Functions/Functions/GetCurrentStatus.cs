using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Models;
using AIAgents.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// HTTP trigger that returns the current pipeline status for the dashboard.
/// GET /api/status
/// </summary>
public sealed class GetCurrentStatus
{
    private readonly ILogger<GetCurrentStatus> _logger;
    private readonly IActivityLogger _activityLogger;
    private readonly IAgentTaskQueue _taskQueue;
    private readonly IAzureDevOpsClient _adoClient;

    public GetCurrentStatus(
        ILogger<GetCurrentStatus> logger,
        IActivityLogger activityLogger,
        IAgentTaskQueue taskQueue,
        IAzureDevOpsClient adoClient)
    {
        _logger = logger;
        _activityLogger = activityLogger;
        _taskQueue = taskQueue;
        _adoClient = adoClient;
    }

    [Function("GetCurrentStatus")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Dashboard status request received");

        var storyActivity = await _activityLogger.GetRecentForStoriesAsync(500, cancellationToken) ?? [];
        var recentActivity = await _activityLogger.GetRecentAsync(50, cancellationToken) ?? [];
        if (storyActivity.Count == 0 && recentActivity.Count > 0)
        {
            storyActivity = recentActivity;
        }

        // Peek at queued tasks waiting to be processed
        IReadOnlyList<AgentTask> queuedTasks;
        try
        {
            queuedTasks = await _taskQueue.PeekAsync(32, cancellationToken);
        }
        catch
        {
            queuedTasks = [];
        }

        // Build story statuses from activity log
        var storyStatuses = BuildStoryStatuses(storyActivity);
        await PopulateStoryTitlesAsync(storyStatuses, cancellationToken);

        // Identify the currently active work item (most recent story with an in-progress agent)
        var activeStory = storyStatuses.FirstOrDefault(s =>
            s.CurrentAgent != null || s.Agents.Values.Any(a => a == "in_progress"));

        CurrentWorkItemInfo? currentWorkItem = null;
        if (activeStory != null)
        {
            var firstActivity = recentActivity
                .Where(a => a.WorkItemId == activeStory.WorkItemId)
                .OrderBy(a => a.Timestamp)
                .FirstOrDefault();

            var elapsed = firstActivity != null
                ? FormatElapsed(DateTime.UtcNow - firstActivity.Timestamp)
                : null;

            // Fetch work item to get autonomy level
            string? autonomyLevel = null;
            try
            {
                var workItem = await _adoClient.GetWorkItemAsync(activeStory.WorkItemId, cancellationToken);
                autonomyLevel = workItem.AutonomyLevel.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch autonomy level for work item {WorkItemId}", activeStory.WorkItemId);
            }

            currentWorkItem = new CurrentWorkItemInfo
            {
                Id = activeStory.WorkItemId,
                Title = string.IsNullOrWhiteSpace(activeStory.Title)
                    ? $"US-{activeStory.WorkItemId}"
                    : activeStory.Title,
                State = !string.IsNullOrWhiteSpace(activeStory.CurrentAiAgent)
                    ? activeStory.CurrentAiAgent
                    : activeStory.CurrentAgent != null
                        ? $"{activeStory.CurrentAgent} Agent"
                        : null,
                AutonomyLevel = autonomyLevel,
                ElapsedTime = elapsed
            };
        }

        var adoProjectName = Environment.GetEnvironmentVariable("AZURE_DEVOPS_PROJECT")
            ?? Environment.GetEnvironmentVariable("AzureDevOps__Project")
            ?? Environment.GetEnvironmentVariable("AzureDevOps:Project");

        var gitHubOwner = Environment.GetEnvironmentVariable("GITHUB_ORG")
            ?? Environment.GetEnvironmentVariable("GitHub__Owner")
            ?? Environment.GetEnvironmentVariable("GitHub:Owner");

        var gitHubRepo = Environment.GetEnvironmentVariable("GITHUB_REPO")
            ?? Environment.GetEnvironmentVariable("GitHub__Repo")
            ?? Environment.GetEnvironmentVariable("GitHub:Repo");

        if (string.IsNullOrWhiteSpace(gitHubOwner) || string.IsNullOrWhiteSpace(gitHubRepo))
        {
            var repositoryUrl = Environment.GetEnvironmentVariable("Git__RepositoryUrl")
                ?? Environment.GetEnvironmentVariable("Git:RepositoryUrl");

            if (!string.IsNullOrWhiteSpace(repositoryUrl) &&
                TryParseGitHubRepo(repositoryUrl, out var parsedOwner, out var parsedRepo))
            {
                gitHubOwner ??= parsedOwner;
                gitHubRepo ??= parsedRepo;
            }
        }

        var status = new DashboardStatus
        {
            AdoProjectName = string.IsNullOrWhiteSpace(adoProjectName)
                ? null
                : adoProjectName,
            GitHubOwner = string.IsNullOrWhiteSpace(gitHubOwner)
                ? null
                : gitHubOwner,
            GitHubRepo = string.IsNullOrWhiteSpace(gitHubRepo)
                ? null
                : gitHubRepo,
            CurrentWorkItem = currentWorkItem,
            Stories = storyStatuses,
            Stats = new DashboardStats
            {
                StoriesProcessed = storyStatuses.Count(s =>
                    s.Agents.Values.All(a => a is "completed" or "failed")),
                AgentsActive = storyStatuses.Count(s =>
                    s.Agents.Values.Any(a => a == "in_progress")),
                SuccessRate = CalculateSuccessRate(storyStatuses),
                AvgProcessingTime = CalculateAverageProcessingTime(storyStatuses),
                TotalTokens = storyStatuses.Sum(s => s.TokenUsage?.TotalTokens ?? 0),
                TotalCost = storyStatuses.Sum(s => s.TokenUsage?.TotalCost ?? 0m)
            },
            RecentActivity = recentActivity,
            QueuedTasks = queuedTasks.Select(t => new QueuedTaskInfo
            {
                WorkItemId = t.WorkItemId,
                AgentType = t.AgentType.ToString(),
                EnqueuedAt = t.EnqueuedAt
            }).ToList()
        };

        return new OkObjectResult(status);
    }

    private static bool TryParseGitHubRepo(string repositoryUrl, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        var trimmed = repositoryUrl.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                owner = segments[0];
                repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? segments[1][..^4]
                    : segments[1];
                return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
            }

            return false;
        }

        // Handle scp-like format git@github.com:owner/repo.git
        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < trimmed.Length - 1)
        {
            var pathPart = trimmed[(colonIndex + 1)..].Trim('/');
            var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                owner = segments[0];
                repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? segments[1][..^4]
                    : segments[1];
                return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
            }
        }

        return false;
    }

    private static List<StoryStatus> BuildStoryStatuses(IReadOnlyList<ActivityEntry> activities)
    {
        var storyGroups = activities
            .GroupBy(a => a.WorkItemId)
            .Where(g => g.Key > 0);

        var statuses = new List<StoryStatus>();

        foreach (var group in storyGroups)
        {
            var agents = new Dictionary<string, string>();
            var agentTimings = new Dictionary<string, AgentTimingDto>();
            var agentDetails = new Dictionary<string, AgentDetailDto>();
            var agentStartTimes = new Dictionary<string, DateTime>();
            string? currentAgent = null;

            foreach (var activity in group.OrderBy(a => a.Timestamp))
            {
                var agent = activity.Agent;
                if (agent == "Orchestrator") continue;

                if (activity.Message.Contains("started", StringComparison.OrdinalIgnoreCase))
                {
                    agents[agent] = "in_progress";
                    currentAgent = agent;
                    agentStartTimes[agent] = activity.Timestamp;
                    // Clear any previous copilot-delegated details when the agent restarts
                    // (e.g., after a Copilot timeout, the agent re-enqueues in agentic mode)
                    agentDetails.Remove(agent);
                }
                else if (activity.Message.Contains("Delegated to @", StringComparison.OrdinalIgnoreCase))
                {
                    // Copilot delegation — mark as copilot_delegated with metadata
                    agents[agent] = "in_progress";
                    currentAgent = agent;

                    // Parse agent name and issue number from message like:
                    //   "Delegated to @copilot (Issue #25). Pipeline paused."
                    var agentName = "copilot";
                    var issueNumber = 0;
                    var delegatedMatch = System.Text.RegularExpressions.Regex.Match(
                        activity.Message, @"Delegated to @(\w+) \(Issue #(\d+)\)");
                    if (delegatedMatch.Success)
                    {
                        agentName = delegatedMatch.Groups[1].Value;
                        int.TryParse(delegatedMatch.Groups[2].Value, out issueNumber);
                    }

                    agentDetails[agent] = new AgentDetailDto
                    {
                        AdditionalData = new Dictionary<string, object>
                        {
                            ["mode"] = "copilot-delegated",
                            ["agent"] = agentName,
                            ["issueNumber"] = issueNumber,
                            ["delegatedAt"] = activity.Timestamp.ToString("O")
                        }
                    };
                }
                else if (activity.Message.Contains("completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (agents.TryGetValue(agent, out var statusBeforeCompleted) &&
                        string.Equals(statusBeforeCompleted, "needs_revision", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep explicit Needs Revision status; generic dispatcher "completed" should not overwrite it.
                        continue;
                    }

                    // Only mark completed if NOT currently delegated to Copilot
                    // (the "Coding agent completed successfully" fires after delegation returns,
                    //  but the agent is still waiting for Copilot's PR)
                    if (agents.TryGetValue(agent, out var currentStatus) &&
                        agentDetails.ContainsKey(agent) &&
                        agentDetails[agent].AdditionalData?.ContainsKey("mode") == true &&
                        (string?)agentDetails[agent].AdditionalData!["mode"] == "copilot-delegated" &&
                        !activity.Message.Contains("Copilot", StringComparison.Ordinal))
                    {
                        // Keep as in_progress (delegated) — don't overwrite with "completed"
                    }
                    else
                    {
                        agents[agent] = "completed";
                        if (currentAgent == agent) currentAgent = null;
                        // Clear copilot-delegated details on completion so spinner stops
                        agentDetails.Remove(agent);
                    }
                    var startedAt = agentStartTimes.TryGetValue(agent, out var st) ? st : (DateTime?)null;
                    var duration = startedAt.HasValue ? (activity.Timestamp - startedAt.Value).TotalSeconds : (double?)null;
                    agentTimings[agent] = new AgentTimingDto
                    {
                        StartedAt = startedAt,
                        CompletedAt = activity.Timestamp,
                        DurationSeconds = duration
                    };

                    if (activity.Message.Contains("Skipping Testing", StringComparison.OrdinalIgnoreCase) ||
                        activity.Message.Contains("Testing skipped", StringComparison.OrdinalIgnoreCase))
                    {
                        agents["Testing"] = "skipped";
                        agentDetails["Testing"] = new AgentDetailDto
                        {
                            AdditionalData = new Dictionary<string, object>
                            {
                                ["skipReason"] = "GitHub Copilot coding agent already validated tests"
                            }
                        };
                    }
                }
                else if (activity.Message.Contains("Needs Revision", StringComparison.OrdinalIgnoreCase) ||
                         activity.Message.Contains("Story Not Ready for Coding", StringComparison.OrdinalIgnoreCase) ||
                         activity.Message.Contains("rejected story", StringComparison.OrdinalIgnoreCase))
                {
                    agents[agent] = "needs_revision";
                    if (currentAgent == agent) currentAgent = null;

                    agentDetails[agent] = new AgentDetailDto
                    {
                        AdditionalData = new Dictionary<string, object>
                        {
                            ["triageResult"] = "rejected",
                            ["note"] = activity.Message
                        }
                    };

                    var startedAt = agentStartTimes.TryGetValue(agent, out var st) ? st : (DateTime?)null;
                    var duration = startedAt.HasValue ? (activity.Timestamp - startedAt.Value).TotalSeconds : (double?)null;
                    agentTimings[agent] = new AgentTimingDto
                    {
                        StartedAt = startedAt,
                        CompletedAt = activity.Timestamp,
                        DurationSeconds = duration
                    };
                }
                else if (activity.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    agents[agent] = "failed";
                    if (currentAgent == agent) currentAgent = null;
                    var startedAt = agentStartTimes.TryGetValue(agent, out var st) ? st : (DateTime?)null;
                    var duration = startedAt.HasValue ? (activity.Timestamp - startedAt.Value).TotalSeconds : (double?)null;
                    agentTimings[agent] = new AgentTimingDto
                    {
                        StartedAt = startedAt,
                        CompletedAt = activity.Timestamp,
                        DurationSeconds = duration
                    };
                }
                else if (activity.Message.Contains("Awaiting human", StringComparison.OrdinalIgnoreCase))
                {
                    // Coding agent handoff — mark as awaiting_code
                    agents[agent] = "awaiting_code";
                    if (currentAgent == agent) currentAgent = null;
                }
                else if (activity.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    // Copilot timed out — clear delegation details
                    agentDetails.Remove(agent);
                }
            }

            // Add timings for in-progress agents (no completedAt yet)
            foreach (var (agent, startTime) in agentStartTimes)
            {
                if (!agentTimings.ContainsKey(agent))
                {
                    agentTimings[agent] = new AgentTimingDto
                    {
                        StartedAt = startTime,
                        CompletedAt = null,
                        DurationSeconds = null
                    };
                }
            }

            var completedCount = agents.Values.Count(v => v is "completed" or "skipped");
            var totalAgents = 6; // Planning, Coding, Testing, Review, Documentation, Deployment
            var progress = (int)((double)completedCount / totalAgents * 100);

            // Aggregate token usage from activity entries (exclude TokenSummary to avoid double-counting)
            var storyActivities = group.ToList();
            var tokenActivities = storyActivities.Where(a => a.Tokens > 0 && a.Agent != "TokenSummary").ToList();
            var totalTokens = tokenActivities.Sum(a => a.Tokens);
            var totalCost = tokenActivities.Sum(a => a.Cost);

            var agentTokens = tokenActivities
                .GroupBy(a => a.Agent)
                .ToDictionary(
                    g => g.Key,
                    g => new AgentTokenUsageDto
                    {
                        TotalTokens = g.Sum(a => a.Tokens),
                        EstimatedCost = g.Sum(a => a.Cost),
                        Model = "",
                        CallCount = g.Count()
                    });

            statuses.Add(new StoryStatus
            {
                WorkItemId = group.Key,
                Title = $"US-{group.Key}",
                CurrentAgent = currentAgent,
                Progress = progress,
                Agents = agents,
                AgentDetails = agentDetails.Count > 0 ? agentDetails : null,
                AgentTimings = agentTimings.Count > 0 ? agentTimings : null,
                TokenUsage = totalTokens > 0 ? new StoryTokenUsageDto
                {
                    TotalTokens = totalTokens,
                    TotalCost = totalCost,
                    Complexity = StoryTokenUsage.ClassifyComplexity(totalTokens),
                    Agents = agentTokens.Count > 0 ? agentTokens : null
                } : null
            });
        }

        return statuses;
    }

    private async Task PopulateStoryTitlesAsync(List<StoryStatus> stories, CancellationToken cancellationToken)
    {
        if (stories.Count == 0)
        {
            return;
        }

        var distinctIds = stories
            .Select(s => s.WorkItemId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (distinctIds.Count == 0)
        {
            return;
        }

        var titleById = new Dictionary<int, string>();
        var stateById = new Dictionary<int, string>();
        var currentAgentById = new Dictionary<int, string?>();

        var fetchTasks = distinctIds.Select(async id =>
        {
            try
            {
                var workItem = await _adoClient.GetWorkItemAsync(id, cancellationToken);
                var title = workItem.Title?.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    lock (titleById)
                    {
                        titleById[id] = title;
                        stateById[id] = workItem.State;
                        currentAgentById[id] = workItem.CurrentAIAgent;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve title for work item {WorkItemId}", id);
            }
        });

        await Task.WhenAll(fetchTasks);

        for (var index = 0; index < stories.Count; index++)
        {
            var story = stories[index];
            var resolvedWorkItemState = stateById.GetValueOrDefault(story.WorkItemId);
            var isAgentFailedState = string.Equals(resolvedWorkItemState, "Agent Failed", StringComparison.OrdinalIgnoreCase);
            var mappedCurrentAgent = currentAgentById.TryGetValue(story.WorkItemId, out var fieldValue)
                ? MapCurrentAgentField(fieldValue)
                : null;
            var mergedAgents = new Dictionary<string, string>(story.Agents, StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(mappedCurrentAgent))
            {
                mergedAgents[mappedCurrentAgent] = isAgentFailedState ? "failed" : "in_progress";
            }

            if (titleById.TryGetValue(story.WorkItemId, out var resolvedTitle))
            {
                stories[index] = new StoryStatus
                {
                    WorkItemId = story.WorkItemId,
                    Title = resolvedTitle,
                    CurrentAgent = mappedCurrentAgent ?? story.CurrentAgent,
                    CurrentAiAgent = currentAgentById.GetValueOrDefault(story.WorkItemId),
                    WorkItemState = resolvedWorkItemState,
                    Progress = story.Progress,
                    Agents = mergedAgents,
                    AgentDetails = story.AgentDetails,
                    TokenUsage = story.TokenUsage,
                    AgentTimings = story.AgentTimings
                };
            }
            else
            {
                stories[index] = new StoryStatus
                {
                    WorkItemId = story.WorkItemId,
                    Title = story.Title,
                    CurrentAgent = mappedCurrentAgent ?? story.CurrentAgent,
                    CurrentAiAgent = currentAgentById.GetValueOrDefault(story.WorkItemId),
                    WorkItemState = resolvedWorkItemState,
                    Progress = story.Progress,
                    Agents = mergedAgents,
                    AgentDetails = story.AgentDetails,
                    TokenUsage = story.TokenUsage,
                    AgentTimings = story.AgentTimings
                };
            }
        }
    }

    private static double CalculateSuccessRate(List<StoryStatus> stories)
    {
        if (stories.Count == 0) return 100.0;

        var completed = stories.Count(s =>
            s.Agents.Values.Any() && s.Agents.Values.All(a => a == "completed"));
        var failed = stories.Count(s =>
            s.Agents.Values.Any(a => a == "failed"));

        var total = completed + failed;
        return total > 0 ? Math.Round((double)completed / total * 100, 1) : 100.0;
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private static string CalculateAverageProcessingTime(List<StoryStatus> stories)
    {
        var completedStories = stories
            .Where(s => s.Agents.Values.Any() && s.Agents.Values.All(a => a is "completed" or "failed"))
            .ToList();

        if (completedStories.Count == 0)
        {
            return "N/A";
        }

        var storyDurations = completedStories
            .Select(s => s.AgentTimings?.Values
                .Where(t => t.DurationSeconds.HasValue && t.DurationSeconds.Value > 0)
                .Sum(t => t.DurationSeconds ?? 0) ?? 0)
            .Where(seconds => seconds > 0)
            .ToList();

        if (storyDurations.Count == 0)
        {
            return "N/A";
        }

        var averageSeconds = storyDurations.Average();
        return FormatElapsed(TimeSpan.FromSeconds(averageSeconds));
    }

    private static string? MapCurrentAgentField(string? currentAIAgent)
    {
        if (string.IsNullOrWhiteSpace(currentAIAgent))
        {
            return null;
        }

        return currentAIAgent.Trim().ToLowerInvariant() switch
        {
            "planning agent" => "Planning",
            "coding agent" => "Coding",
            "testing agent" => "Testing",
            "review agent" => "Review",
            "documentation agent" => "Documentation",
            "deploy agent" => "Deployment",
            "deployment agent" => "Deployment",
            _ => null
        };
    }
}
