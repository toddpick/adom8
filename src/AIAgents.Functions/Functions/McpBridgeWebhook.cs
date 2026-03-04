using System.Text.Json;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Function-key secured endpoints intended for MCP tool calls during GitHub Copilot coding sessions.
/// These endpoints provide deterministic Azure DevOps updates for stage transitions, comments, and stage events.
/// </summary>
public sealed class McpBridgeWebhook
{
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IActivityLogger _activityLogger;
    private readonly ILogger<McpBridgeWebhook> _logger;

    public McpBridgeWebhook(
        IAzureDevOpsClient adoClient,
        IActivityLogger activityLogger,
        ILogger<McpBridgeWebhook> logger)
    {
        _adoClient = adoClient;
        _activityLogger = activityLogger;
        _logger = logger;
    }

    [Function("McpSetStage")]
    public async Task<IActionResult> SetStageAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "mcp/set-stage")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var payload = await ReadJsonAsync<McpSetStageRequest>(req, cancellationToken);
        if (payload is null || payload.WorkItemId <= 0 || string.IsNullOrWhiteSpace(payload.Stage))
        {
            return new BadRequestObjectResult(new { error = "workItemId and stage are required." });
        }

        var mapping = MapStage(payload.Stage);
        if (mapping is null)
        {
            return new BadRequestObjectResult(new
            {
                error = "Unsupported stage.",
                supportedStages = new[] { "Planning", "Coding", "Testing", "Review", "Documentation", "Deployment", "NeedsInfo", "Done" }
            });
        }

        if (!string.IsNullOrWhiteSpace(mapping.CurrentAgent))
        {
            await UpdateCurrentAgentAsync(payload.WorkItemId, mapping.CurrentAgent, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(payload.FromStage))
        {
            await _adoClient.UpdateWorkItemFieldAsync(
                payload.WorkItemId,
                CustomFieldNames.Paths.LastAgent,
                payload.FromStage,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(payload.Message))
        {
            await _adoClient.AddWorkItemCommentAsync(payload.WorkItemId, payload.Message, cancellationToken);
        }

        await _activityLogger.LogAsync(
            "McpBridge",
            payload.WorkItemId,
            $"MCP stage update: {payload.FromStage ?? "(unknown)"} -> {payload.Stage}",
            payload.Severity ?? "info",
            cancellationToken);

        _logger.LogInformation(
            "MCP stage update applied for WI-{WorkItemId}: {FromStage} -> {ToStage}",
            payload.WorkItemId,
            payload.FromStage ?? "(unknown)",
            payload.Stage);

        return new OkObjectResult(new
        {
            status = "ok",
            payload.WorkItemId,
            stage = payload.Stage,
            mappedState = mapping.State,
            mappedCurrentAgent = mapping.CurrentAgent
        });
    }

    [Function("McpAddComment")]
    public async Task<IActionResult> AddCommentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "mcp/add-comment")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var payload = await ReadJsonAsync<McpAddCommentRequest>(req, cancellationToken);
        if (payload is null || payload.WorkItemId <= 0 || string.IsNullOrWhiteSpace(payload.Comment))
        {
            return new BadRequestObjectResult(new { error = "workItemId and comment are required." });
        }

        await _adoClient.AddWorkItemCommentAsync(payload.WorkItemId, payload.Comment, cancellationToken);

        await _activityLogger.LogAsync(
            "McpBridge",
            payload.WorkItemId,
            $"MCP comment added ({payload.Source ?? "copilot-session"})",
            "info",
            cancellationToken);

        return new OkObjectResult(new
        {
            status = "ok",
            payload.WorkItemId
        });
    }

    [Function("McpStageEvent")]
    public async Task<IActionResult> StageEventAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "mcp/stage-event")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var payload = await ReadJsonAsync<McpStageEventRequest>(req, cancellationToken);
        if (payload is null || payload.WorkItemId <= 0 || string.IsNullOrWhiteSpace(payload.EventType) || string.IsNullOrWhiteSpace(payload.Message))
        {
            return new BadRequestObjectResult(new { error = "workItemId, eventType, and message are required." });
        }

        await _activityLogger.LogAsync(
            "McpBridge",
            payload.WorkItemId,
            $"MCP event [{payload.EventType}] {payload.Message}",
            payload.Severity ?? "info",
            cancellationToken);

        if (payload.AddAdoComment)
        {
            await _adoClient.AddWorkItemCommentAsync(payload.WorkItemId, payload.Message, cancellationToken);
        }

        return new OkObjectResult(new
        {
            status = "ok",
            payload.WorkItemId,
            payload.EventType
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpRequest req, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(body)
                ? default
                : JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private async Task UpdateCurrentAgentAsync(int workItemId, string currentAgent, CancellationToken cancellationToken)
    {
        try
        {
            await _adoClient.UpdateWorkItemFieldAsync(
                workItemId,
                CustomFieldNames.Paths.CurrentAIAgent,
                currentAgent,
                cancellationToken);
        }
        catch (Exception ex) when (string.Equals(currentAgent, AIPipelineNames.CurrentAgentValues.Deployment, StringComparison.OrdinalIgnoreCase))
        {
            // Some ADO projects use "Deploy Agent" in the picklist instead of "Deployment Agent".
            _logger.LogWarning(
                ex,
                "Failed to set Current AI Agent to '{CurrentAgent}' for WI-{WorkItemId}; retrying with legacy value 'Deploy Agent'",
                currentAgent,
                workItemId);

            await _adoClient.UpdateWorkItemFieldAsync(
                workItemId,
                CustomFieldNames.Paths.CurrentAIAgent,
                "Deploy Agent",
                cancellationToken);
        }
    }

    internal static StageMapping? MapStage(string stage) => stage.Trim().ToLowerInvariant() switch
    {
        "planning" => new StageMapping(null, AIPipelineNames.CurrentAgentValues.Planning),
        "coding" => new StageMapping(null, AIPipelineNames.CurrentAgentValues.Coding),
        "testing" => new StageMapping(null, AIPipelineNames.CurrentAgentValues.Testing),
        "review" => new StageMapping(null, AIPipelineNames.CurrentAgentValues.Review),
        "documentation" => new StageMapping(null, AIPipelineNames.CurrentAgentValues.Documentation),
        "deployment" or "deploy" => new StageMapping(null, AIPipelineNames.CurrentAgentValues.Deployment),
        "needsinfo" or "needs_info" or "needs revision" => new StageMapping(null, null),
        "done" => new StageMapping(null, null),
        _ => null
    };

    internal sealed record StageMapping(string? State, string? CurrentAgent);

    private sealed record McpSetStageRequest
    {
        public int WorkItemId { get; init; }
        public string? RunId { get; init; }
        public string? FromStage { get; init; }
        public string? Stage { get; init; }
        public string? Message { get; init; }
        public string? Severity { get; init; }
        public string? IdempotencyKey { get; init; }
        public string? Source { get; init; }
    }

    private sealed record McpAddCommentRequest
    {
        public int WorkItemId { get; init; }
        public string? Comment { get; init; }
        public string? Source { get; init; }
        public string? RunId { get; init; }
    }

    private sealed record McpStageEventRequest
    {
        public int WorkItemId { get; init; }
        public string? EventType { get; init; }
        public string? Message { get; init; }
        public string? Severity { get; init; }
        public string? RunId { get; init; }
        public bool AddAdoComment { get; init; }
    }
}
