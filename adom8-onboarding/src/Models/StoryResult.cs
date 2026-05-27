namespace ADOm8.Onboarding.Models;

public sealed record StoryResult(int StoryId, string StoryUrl, string AgentTabUrl);
public sealed record AdoResult(string ProjectId, bool WebhooksRegistered, string InitiativeAreaPath);
public sealed record InfraResult(string ResourceGroupName, string FunctionAppName, string DashboardUrl, string ServiceBusTopic);

public sealed record ProvisioningInput(string ProvisioningId, string ProjectId, ProjectRegistrationRequest Request);
public sealed record FinalizeInput(ProvisioningInput Input, InfraResult Infra, AdoResult Ado, StoryResult Story);
