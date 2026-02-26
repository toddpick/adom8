namespace AIAgents.Core.Models;

/// <summary>
/// Result of repository preflight sizing analysis.
/// </summary>
public sealed record RepositorySizingResult
{
    public bool CanProceed { get; init; }
    public bool CheckPerformed { get; init; }
    public bool TreeWasTruncated { get; init; }
    public int FileCount { get; init; }
    public long EstimatedWorkingTreeBytes { get; init; }
    public long EstimatedBinaryBytes { get; init; }
    public string Message { get; init; } = string.Empty;

    public static RepositorySizingResult Skipped(string message) => new()
    {
        CanProceed = true,
        CheckPerformed = false,
        Message = message
    };
}
