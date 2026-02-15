using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;

namespace AIAgents.Core.Services;

/// <summary>
/// Azure DevOps client using the official SDK (Microsoft.TeamFoundationServer.Client).
/// The VssConnection is created once and reused for the lifetime of the service.
/// </summary>
public sealed class AzureDevOpsClient : IAzureDevOpsClient, IDisposable
{
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsClient> _logger;
    private readonly Lazy<VssConnection> _connection;

    public AzureDevOpsClient(
        IOptions<AzureDevOpsOptions> options,
        ILogger<AzureDevOpsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connection = new Lazy<VssConnection>(() =>
        {
            var credentials = new VssBasicCredential(string.Empty, _options.Pat);
            return new VssConnection(new Uri(_options.OrganizationUrl), credentials);
        });
    }

    public async Task<StoryWorkItem> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching work item {WorkItemId}", workItemId);

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);

        var workItem = await client.GetWorkItemAsync(
            workItemId,
            expand: WorkItemExpand.All,
            cancellationToken: cancellationToken);

        return MapToStoryWorkItem(workItem);
    }

    public async Task UpdateWorkItemStateAsync(int workItemId, string newState, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating work item {WorkItemId} state to '{NewState}'", workItemId, newState);

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Replace,
                Path = "/fields/System.State",
                Value = newState
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public async Task AddWorkItemCommentAsync(int workItemId, string comment, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding comment to work item {WorkItemId}", workItemId);

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/fields/System.History",
                Value = comment
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public async Task UpdateWorkItemFieldAsync(
        int workItemId,
        string fieldPath,
        object value,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating field '{FieldPath}' on work item {WorkItemId}", fieldPath, workItemId);

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Replace,
                Path = fieldPath,
                Value = value
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public async Task UpdateWorkItemFieldsAsync(
        int workItemId,
        IDictionary<string, object> fieldUpdates,
        CancellationToken cancellationToken = default)
    {
        if (fieldUpdates.Count == 0) return;

        _logger.LogDebug("Updating {Count} fields on work item {WorkItemId}", fieldUpdates.Count, workItemId);

        var patchDocument = new JsonPatchDocument();
        foreach (var (path, value) in fieldUpdates)
        {
            patchDocument.Add(new JsonPatchOperation
            {
                Operation = Operation.Replace,
                Path = path,
                Value = value
            });
        }

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }

    private static StoryWorkItem MapToStoryWorkItem(WorkItem workItem)
    {
        var fields = workItem.Fields;

        return new StoryWorkItem
        {
            Id = workItem.Id ?? 0,
            Title = GetField<string>(fields, "System.Title") ?? "Untitled",
            Description = GetField<string>(fields, "System.Description"),
            AcceptanceCriteria = GetField<string>(fields, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            State = GetField<string>(fields, "System.State") ?? "New",
            AssignedTo = GetIdentityField(fields, "System.AssignedTo"),
            AreaPath = GetField<string>(fields, "System.AreaPath"),
            IterationPath = GetField<string>(fields, "System.IterationPath"),
            StoryPoints = GetField<double?>(fields, "Microsoft.VSTS.Scheduling.StoryPoints") is double sp
                ? (int)sp : null,
            Tags = GetField<string>(fields, "System.Tags")?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [],
            CreatedDate = GetField<DateTime>(fields, "System.CreatedDate"),
            ChangedDate = GetField<DateTime>(fields, "System.ChangedDate"),

            // AI Input Fields
            AutonomyLevel = GetField<double?>(fields, CustomFieldNames.AutonomyLevel) is double al
                ? (int)al : 3,
            MinimumReviewScore = GetField<double?>(fields, CustomFieldNames.MinimumReviewScore) is double mrs
                ? (int)mrs : 85,

            // Per-Story Model Override Fields
            AIModelTier = GetField<string>(fields, CustomFieldNames.ModelTier),
            AIPlanningModel = GetField<string>(fields, CustomFieldNames.PlanningModel),
            AICodingModel = GetField<string>(fields, CustomFieldNames.CodingModel),
            AITestingModel = GetField<string>(fields, CustomFieldNames.TestingModel),
            AIReviewModel = GetField<string>(fields, CustomFieldNames.ReviewModel),
            AIDocumentationModel = GetField<string>(fields, CustomFieldNames.DocumentationModel),

            // AI Output Fields (written by agents, readable for dashboards/queries)
            AITokensUsed = GetField<double?>(fields, CustomFieldNames.TokensUsed) is double tu
                ? (int)tu : null,
            AICost = GetField<string>(fields, CustomFieldNames.Cost),
            AIComplexity = GetField<string>(fields, CustomFieldNames.Complexity),
            AIModel = GetField<string>(fields, CustomFieldNames.Model),
            AIReviewScore = GetField<double?>(fields, CustomFieldNames.ReviewScore) is double rs
                ? (int)rs : null,
            AIProcessingTime = GetField<double?>(fields, CustomFieldNames.ProcessingTime) is double pt
                ? (decimal)pt : null,
            AIFilesGenerated = GetField<double?>(fields, CustomFieldNames.FilesGenerated) is double fg
                ? (int)fg : null,
            AITestsGenerated = GetField<double?>(fields, CustomFieldNames.TestsGenerated) is double tg
                ? (int)tg : null,
            AIPRNumber = GetField<double?>(fields, CustomFieldNames.PRNumber) is double prn
                ? (int)prn : null,
            AILastAgent = GetField<string>(fields, CustomFieldNames.LastAgent),
            AICriticalIssues = GetField<double?>(fields, CustomFieldNames.CriticalIssues) is double ci
                ? (int)ci : null,
            AIDeploymentDecision = GetField<string>(fields, CustomFieldNames.DeploymentDecision)
        };
    }

    private static T? GetField<T>(IDictionary<string, object> fields, string key)
    {
        return fields.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    private static string? GetIdentityField(IDictionary<string, object> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value)) return null;

        return value switch
        {
            IdentityRef identity => identity.DisplayName,
            string s => s,
            _ => value?.ToString()
        };
    }
}
