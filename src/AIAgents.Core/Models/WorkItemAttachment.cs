namespace AIAgents.Core.Models;

/// <summary>
/// Represents an attachment referenced by an Azure DevOps work item.
/// </summary>
public sealed record WorkItemAttachment
{
    /// <summary>Absolute URL to the attachment content.</summary>
    public required string Url { get; init; }

    /// <summary>Best-effort file name for the attachment.</summary>
    public required string FileName { get; init; }

    /// <summary>True when the attachment appears to be an image.</summary>
    public bool IsImage { get; init; }

    /// <summary>True when the attachment appears to be a document type the agents should inspect.</summary>
    public bool IsDocument { get; init; }
}
