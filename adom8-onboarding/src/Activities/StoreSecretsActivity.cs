using System.Text.Json;
using ADOm8.Onboarding.Models;
using ADOm8.Onboarding.Services;
using Microsoft.Azure.Functions.Worker;

namespace ADOm8.Onboarding.Activities;

public sealed class StoreSecretsActivity
{
    private readonly IKeyVaultService _keyVaultService;

    public StoreSecretsActivity(IKeyVaultService keyVaultService)
    {
        _keyVaultService = keyVaultService;
    }

    [Function("StoreSecrets")]
    public async Task Run([ActivityTrigger] ProvisioningInput input)
    {
        var prefix = input.ProjectId;
        await _keyVaultService.SetSecretAsync($"{prefix}-ado-pat", input.Request.AdoPat);
        await _keyVaultService.SetSecretAsync($"{prefix}-github-pat", input.Request.GithubPat);
        await _keyVaultService.SetSecretAsync($"{prefix}-config", JsonSerializer.Serialize(input.Request));

        if (input.Request.Capabilities.SelfHealing && !string.IsNullOrWhiteSpace(input.Request.Capabilities.SelfHealingConnectionString))
        {
            await _keyVaultService.SetSecretAsync($"{prefix}-sb-connection", input.Request.Capabilities.SelfHealingConnectionString);
        }
    }
}
