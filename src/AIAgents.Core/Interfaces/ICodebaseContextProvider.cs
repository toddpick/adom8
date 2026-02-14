namespace AIAgents.Core.Interfaces;

/// <summary>
/// Provides intelligent context loading from the .agent/ documentation folder.
/// Each agent uses this at execution start to include relevant codebase context
/// in its AI prompts, making generated code architecture-aware.
/// </summary>
public interface ICodebaseContextProvider
{
    /// <summary>
    /// Loads relevant codebase context for a work item.
    /// Selectively loads CONTEXT_INDEX.md (always), CODING_STANDARDS.md,
    /// and any FEATURES/*.md files whose keywords match the work item text.
    /// </summary>
    /// <param name="repositoryPath">Local path to the cloned repository.</param>
    /// <param name="workItemTitle">Title of the work item (used for keyword matching).</param>
    /// <param name="workItemDescription">Description of the work item (used for keyword matching).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A formatted context string ready to include in AI prompts, or empty string
    /// if no .agent/ folder exists.
    /// </returns>
    Task<string> LoadRelevantContextAsync(
        string repositoryPath,
        string workItemTitle,
        string? workItemDescription,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the .agent/ documentation folder exists in the repository.
    /// </summary>
    Task<bool> HasCodebaseDocumentationAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
