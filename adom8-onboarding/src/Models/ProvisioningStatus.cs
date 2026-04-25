namespace ADOm8.Onboarding.Models;

public sealed record LiveLinks(string? DashboardUrl, string? AgentTabUrl, string? InitStoryUrl);

public sealed record ProvisioningStatus
{
    public required string ProvisioningId { get; init; }
    public required string Stage { get; init; }
    public int ProgressPercent { get; init; }
    public required string RuntimeStatus { get; init; }
    public string? Error { get; init; }
    public LiveLinks Links { get; init; } = new(null, null, null);
}
