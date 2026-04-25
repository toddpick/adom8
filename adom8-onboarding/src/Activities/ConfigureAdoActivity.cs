using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;

namespace ADOm8.Onboarding.Activities;

public sealed class ConfigureAdoActivity
{
    [Function("ConfigureAdo")]
    public Task<AdoResult> Run([ActivityTrigger] ProvisioningInput input)
    {
        // Stub for idempotent webhook registration/custom field creation.
        return Task.FromResult(new AdoResult(input.Request.AdoProject, WebhooksRegistered: true, InitiativeAreaPath: input.Request.AdoProject));
    }
}
