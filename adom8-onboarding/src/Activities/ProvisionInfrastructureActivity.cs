using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ADOm8.Onboarding.Activities;

public sealed class ProvisionInfrastructureActivity
{
    private readonly ILogger<ProvisionInfrastructureActivity> _logger;

    public ProvisionInfrastructureActivity(ILogger<ProvisionInfrastructureActivity> logger)
    {
        _logger = logger;
    }

    [Function("ProvisionInfrastructure")]
    public Task<InfraResult> Run([ActivityTrigger] ProvisioningInput input)
    {
        // Stub for ARM/Bicep deployment via managed identity.
        _logger.LogInformation("ProvisionInfrastructure requested for {ProjectId}", input.ProjectId);

        return Task.FromResult(new InfraResult(
            $"rg-adom8-{input.ProjectId}",
            $"fa-adom8-{input.ProjectId}",
            $"https://dashboard.adom8.dev/{input.ProjectId}",
            $"adom8-{input.ProjectId}"));
    }
}
