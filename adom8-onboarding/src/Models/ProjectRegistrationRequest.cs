namespace ADOm8.Onboarding.Models;

public sealed record CapabilityOptions
{
    public bool SelfHealing { get; init; }
    public string? SelfHealingConnectionString { get; init; }
    public bool PlaywrightTesting { get; init; }
}

public sealed record ProjectRegistrationRequest
{
    public required string DisplayName { get; init; }
    public required string AdoOrg { get; init; }
    public required string AdoProject { get; init; }
    public required string AdoPat { get; init; }
    public required string GithubRepo { get; init; }
    public required string GithubPat { get; init; }
    public string PrimaryBranch { get; init; } = "main";
    public int DefaultAutonomyLevel { get; init; } = 3;
    public CapabilityOptions Capabilities { get; init; } = new();
}
