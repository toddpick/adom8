using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Services;

/// <summary>
/// Tracks Copilot coding agent delegations in Azure Table Storage.
/// Used to correlate incoming Copilot PRs with pipeline work items
/// and to detect timed-out delegations for fallback.
/// </summary>
public interface ICopilotDelegationService
{
    /// <summary>Records a new delegation.</summary>
    Task CreateAsync(CopilotDelegation delegation, CancellationToken cancellationToken = default);

    /// <summary>Finds a delegation by work item ID.</summary>
    Task<CopilotDelegation?> GetByWorkItemIdAsync(int workItemId, CancellationToken cancellationToken = default);

    /// <summary>Finds a delegation by GitHub Issue number.</summary>
    Task<CopilotDelegation?> GetByIssueNumberAsync(int issueNumber, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing delegation (e.g., mark completed).</summary>
    Task UpdateAsync(CopilotDelegation delegation, CancellationToken cancellationToken = default);

    /// <summary>Gets all pending delegations older than the specified threshold.</summary>
    Task<IReadOnlyList<CopilotDelegation>> GetTimedOutAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CopilotDelegation>> GetPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A single Copilot coding agent delegation record.
/// </summary>
public sealed class CopilotDelegation
{
    public required int WorkItemId { get; init; }
    public int IssueNumber { get; set; }
    public required string CorrelationId { get; init; }
    public required string BranchName { get; init; }
    public DateTime DelegatedAt { get; init; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending"; // Pending, Completed, TimedOut, Failed
    public int? CopilotPrNumber { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Azure Table Storage implementation of <see cref="ICopilotDelegationService"/>.
/// Uses the same storage account as the activity logger — no new infrastructure.
/// </summary>
public sealed class TableStorageCopilotDelegationService : ICopilotDelegationService
{
    private const string TableName = "CopilotDelegations";
    private const string PartitionKey = "delegation";

    private readonly TableClient _tableClient;
    private readonly ILogger<TableStorageCopilotDelegationService> _logger;

    public TableStorageCopilotDelegationService(
        IConfiguration configuration,
        ILogger<TableStorageCopilotDelegationService> logger)
    {
        _logger = logger;

        var connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage connection string is required.");

        _tableClient = new TableClient(connectionString, TableName);
        _tableClient.CreateIfNotExists();
    }

    public async Task CreateAsync(CopilotDelegation delegation, CancellationToken cancellationToken = default)
    {
        var entity = ToEntity(delegation);

        try
        {
            await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            _logger.LogInformation(
                "Created/updated Copilot delegation for WI-{WorkItemId} (Issue #{IssueNumber}, Branch: {Branch})",
                delegation.WorkItemId, delegation.IssueNumber, delegation.BranchName);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to create Copilot delegation for WI-{WorkItemId}", delegation.WorkItemId);
            throw;
        }
    }

    public async Task<CopilotDelegation?> GetByWorkItemIdAsync(int workItemId, CancellationToken cancellationToken = default)
    {
        var rowKey = workItemId.ToString();

        try
        {
            var response = await _tableClient.GetEntityIfExistsAsync<TableEntity>(PartitionKey, rowKey, cancellationToken: cancellationToken);
            return response.HasValue ? FromEntity(response.Value!) : null;
        }
        catch (RequestFailedException)
        {
            return null;
        }
    }

    public async Task<CopilotDelegation?> GetByIssueNumberAsync(int issueNumber, CancellationToken cancellationToken = default)
    {
        var query = _tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{PartitionKey}' and IssueNumber eq {issueNumber}",
            cancellationToken: cancellationToken);

        await foreach (var entity in query)
        {
            return FromEntity(entity);
        }

        return null;
    }

    public async Task UpdateAsync(CopilotDelegation delegation, CancellationToken cancellationToken = default)
    {
        var entity = ToEntity(delegation);
        entity.ETag = ETag.All; // Unconditional update

        try
        {
            await _tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);
            _logger.LogInformation(
                "Updated Copilot delegation for WI-{WorkItemId} (Status: {Status})",
                delegation.WorkItemId, delegation.Status);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to update Copilot delegation for WI-{WorkItemId}", delegation.WorkItemId);
            throw;
        }
    }

    public async Task<IReadOnlyList<CopilotDelegation>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CopilotDelegation>();
        var query = _tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{PartitionKey}' and Status eq 'Pending'",
            cancellationToken: cancellationToken);

        await foreach (var entity in query)
        {
            results.Add(FromEntity(entity));
        }

        return results;
    }

    public async Task<IReadOnlyList<CopilotDelegation>> GetTimedOutAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(timeout);
        var pending = await GetPendingAsync(cancellationToken);
        return pending.Where(d => d.DelegatedAt < cutoff).ToList();
    }

    private static TableEntity ToEntity(CopilotDelegation delegation) => new(PartitionKey, delegation.WorkItemId.ToString())
    {
        ["IssueNumber"] = delegation.IssueNumber,
        ["CorrelationId"] = delegation.CorrelationId,
        ["BranchName"] = delegation.BranchName,
        ["DelegatedAt"] = new DateTimeOffset(DateTime.SpecifyKind(delegation.DelegatedAt, DateTimeKind.Utc)),
        ["Status"] = delegation.Status,
        ["CopilotPrNumber"] = delegation.CopilotPrNumber ?? 0,
        ["CompletedAt"] = delegation.CompletedAt.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(delegation.CompletedAt.Value, DateTimeKind.Utc))
            : (DateTimeOffset?)null
    };

    private static CopilotDelegation FromEntity(TableEntity entity) => new()
    {
        WorkItemId = int.Parse(entity.RowKey!),
        IssueNumber = entity.GetInt32("IssueNumber") ?? 0,
        CorrelationId = entity.GetString("CorrelationId") ?? "",
        BranchName = entity.GetString("BranchName") ?? "",
        DelegatedAt = ParseDateTimeProperty(entity, "DelegatedAt") ?? DateTime.UtcNow,
        Status = entity.GetString("Status") ?? "Pending",
        CopilotPrNumber = entity.GetInt32("CopilotPrNumber") is > 0 ? entity.GetInt32("CopilotPrNumber") : null,
        CompletedAt = ParseDateTimeProperty(entity, "CompletedAt")
    };

    /// <summary>
    /// Safely reads a datetime property that may be stored as either a native DateTimeOffset
    /// or an ISO-8601 string (from older records). GetDateTimeOffset() throws if the property
    /// exists but is stored as a string type, so we must catch and fall back.
    /// </summary>
    private static DateTime? ParseDateTimeProperty(TableEntity entity, string propertyName)
    {
        try
        {
            var dto = entity.GetDateTimeOffset(propertyName);
            if (dto.HasValue) return dto.Value.UtcDateTime;
        }
        catch (InvalidOperationException)
        {
            // Property exists but is stored as string — fall back to string parsing
        }

        var str = entity.GetString(propertyName);
        if (!string.IsNullOrEmpty(str) && DateTime.TryParse(str, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }
}
