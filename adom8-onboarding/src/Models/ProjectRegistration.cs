namespace ADOm8.Onboarding.Models;

public sealed record ProvisionedInfo(string ResourceGroup, string FunctionAppName, string DashboardUrl, string ServiceBusTopic);
public sealed record AdoRegistrationInfo(int InitStoryId, string InitStoryUrl, bool WebhooksRegistered);
public sealed record GithubRegistrationInfo(string AgentTabUrl, string? InitPrUrl);

public sealed record ProjectRegistration
{
    public required string ProjectId { get; init; }
    public required string DisplayName { get; init; }
    public required string Status { get; init; }
    public required string AdoOrg { get; init; }
    public required string AdoProject { get; init; }
    public required string GithubRepo { get; init; }
    public required string PrimaryBranch { get; init; }
    public int DefaultAutonomyLevel { get; init; } = 3;
    public CapabilityOptions Capabilities { get; init; } = new();
    public ProvisionedInfo? Provisioned { get; init; }
    public AdoRegistrationInfo? Ado { get; init; }
    public GithubRegistrationInfo? Github { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset? ProvisioningCompletedAt { get; init; }
}
