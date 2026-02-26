namespace AIAgents.Core.Configuration;

/// <summary>
/// Guardrails for repository preflight sizing checks.
/// Bound to the "RepositoryCapacity" configuration section.
/// </summary>
public sealed class RepositoryCapacityOptions
{
    public const string SectionName = "RepositoryCapacity";

    /// <summary>
    /// Enables preflight checks that can block clone-heavy agent stages.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum estimated working-tree bytes allowed for local clone mode.
    /// </summary>
    public long MaxEstimatedWorkingTreeBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>
    /// Maximum aggregate bytes for binary/non-code assets.
    /// </summary>
    public long MaxBinaryBytes { get; init; } = 150L * 1024 * 1024;

    /// <summary>
    /// Maximum number of files allowed for local clone mode.
    /// </summary>
    public int MaxFileCount { get; init; } = 120_000;

    /// <summary>
    /// When true, blocks clone mode if GitHub returns a truncated recursive tree.
    /// </summary>
    public bool BlockWhenTreeTruncated { get; init; } = true;

    /// <summary>
    /// File extensions treated as binary/non-code for sizing purposes.
    /// </summary>
    public List<string> BinaryExtensions { get; init; } = new()
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".mp4", ".mov", ".avi"
    };
}
