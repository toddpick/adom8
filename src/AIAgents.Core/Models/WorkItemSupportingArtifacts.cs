namespace AIAgents.Core.Models;

/// <summary>
/// Localized supporting artifacts downloaded from an Azure DevOps work item.
/// </summary>
public sealed record WorkItemSupportingArtifacts
{
    /// <summary>
    /// Repository-relative folder containing downloaded supporting files for this story.
    /// Empty when no files were downloaded.
    /// </summary>
    public string StoryDocumentsFolder { get; init; } = string.Empty;

    /// <summary>
    /// Repository-relative paths to image files.
    /// </summary>
    public IReadOnlyList<string> ImagePaths { get; init; } = [];

    /// <summary>
    /// Repository-relative paths to non-image document files.
    /// </summary>
    public IReadOnlyList<string> DocumentPaths { get; init; } = [];

    /// <summary>
    /// Repository-relative paths to all downloaded supporting files.
    /// </summary>
    public IReadOnlyList<string> AllPaths { get; init; } = [];
}