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

    public GetCurrentStatus(
        ILogger<GetCurrentStatus> logger,
        IActivityLogger activityLogger,
        IAgentTaskQueue taskQueue)
    {
        _logger = logger;
        _activityLogger = activityLogger;
        _taskQueue = taskQueue;
    }

    [Function("GetCurrentStatus")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Dashboard status request received");

        var recentActivity = await _activityLogger.GetRecentAsync(50, cancellationToken);

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
        var storyStatuses = BuildStoryStatuses(recentActivity);

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

            currentWorkItem = new CurrentWorkItemInfo
            {
                Id = activeStory.WorkItemId,
                Title = activeStory.Title.StartsWith("US-")
                    ? activeStory.Title
                    : $"US-{activeStory.WorkItemId}",
                State = activeStory.CurrentAgent != null
                    ? $"AI {activeStory.CurrentAgent}"
                    : null,
                ElapsedTime = elapsed
            };
        }

        var status = new DashboardStatus
        {
            CurrentWorkItem = currentWorkItem,
            Stories = storyStatuses,
            Stats = new DashboardStats
            {
                StoriesProcessed = storyStatuses.Count(s =>
                    s.Agents.Values.All(a => a is "completed" or "failed")),
                AgentsActive = storyStatuses.Count(s =>
                    s.Agents.Values.Any(a => a == "in_progress")),
                SuccessRate = CalculateSuccessRate(storyStatuses),
                AvgProcessingTime = "N/A",
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
                }
                else if (activity.Message.Contains("completed", StringComparison.OrdinalIgnoreCase))
                {
                    agents[agent] = "completed";
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

            var completedCount = agents.Values.Count(v => v == "completed");
            var totalAgents = 6; // Planning, Coding, Testing, Review, Documentation, Deployment
            var progress = (int)((double)completedCount / totalAgents * 100);

            // Aggregate token usage from activity entries
            var storyActivities = group.ToList();
            var totalTokens = storyActivities.Sum(a => a.Tokens);
            var totalCost = storyActivities.Sum(a => a.Cost);

            var agentTokens = storyActivities
                .Where(a => a.Tokens > 0)
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
}
