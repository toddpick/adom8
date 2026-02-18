using AIAgents.Core.Models;

namespace AIAgents.Core.Interfaces;

/// <summary>
/// Abstracts Azure DevOps work-item operations.
/// Repository-level operations (PRs, pipelines) are in <see cref="IRepositoryProvider"/>.
/// </summary>
public interface IAzureDevOpsClient
{
    /// <summary>
    /// Retrieves a work item by its ID.
    /// </summary>
    Task<StoryWorkItem> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the state of a work item (e.g., "Story Planning" → "AI Code").
    /// </summary>
    Task UpdateWorkItemStateAsync(int workItemId, string newState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a comment to a work item.
    /// </summary>
    Task AddWorkItemCommentAsync(int workItemId, string comment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a work item field with a JSON patch operation.
    /// </summary>
    Task UpdateWorkItemFieldAsync(
        int workItemId,
        string fieldPath,
        object value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates multiple work item fields in a single API call.
    /// Keys are JSON patch paths (e.g., "/fields/Custom.AITokensUsed"), values are the field values.
    /// </summary>
    Task UpdateWorkItemFieldsAsync(
        int workItemId,
        IDictionary<string, object> fieldUpdates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new work item (User Story) with the given title, description, and state.
    /// Returns the ID of the newly created work item.
    /// </summary>
    Task<int> CreateWorkItemAsync(
        string title,
        string description,
        string state,
        CancellationToken cancellationToken = default);
}
